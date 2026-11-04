using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskBuddy.Services;

/// <summary>
/// DeskBuddy 自有磁盘索引引擎（Listary DiskSearch 同技术路线）：直读 NTFS USN 日志建内存索引，
/// 不依赖 Windows Search。
///
/// 存储结构（紧凑数组，500 万条量级约 400MB，而不是 Dictionary 方案的 1.8GB）：
///   slot → fileRef / parentSlot / (nameOff,nameLen) → 名字统一放一个 char[] 缓冲
/// 持久化：整份索引写成一个二进制文件，下次启动直接读回（≈2s）而不用重新枚举 USN（≈11s）；
///         两次启动之间发生的变化，用 USN 日志的增量（只读到上次的游标之后）补齐。
/// 兜底：文件缺失/版本不符/卷变更/读取异常 → 直接全量重建（老路径，行为不变）。
/// </summary>
public static class UsnIndex
{
    // ==================== 紧凑存储 ====================

    private static long[] _ref = Array.Empty<long>();        // slot -> MFN 引用
    private static int[] _parent = Array.Empty<int>();       // slot -> 父目录 slot（-1 = 卷根）
    private static int[] _nameOff = Array.Empty<int>();      // slot -> 名字在 _nameBuf 的起始下标
    private static ushort[] _nameLen = Array.Empty<ushort>();// slot -> 名字字符数
    private static byte[] _vol = Array.Empty<byte>();        // slot -> 所属卷下标
    private static bool[] _isFile = Array.Empty<bool>();     // slot -> 是否普通文件（目录只用于拼路径，不进搜索桶）
    private static char[] _nameBuf = Array.Empty<char>();    // 所有名字连在一起
    private static bool[] _alive = Array.Empty<bool>();      // 增量删除用的墓碑
    private static int _count;                                // 已用 slot 数

    // 卷：盘符 + USN 游标（+ JournalID 校验）
    private sealed class Volume { public string Drive = ""; public long JournalId; public long NextUsn; }
    private static List<Volume> _vols = new();
    private static Dictionary<int, string> _rootDrive = new();  // slot -> "E:"

    // 查询桶：前 2 字符（快） + 首字符（子串兜底）
    private static Dictionary<string, List<int>> _prefix2 = new(StringComparer.Ordinal);
    private static Dictionary<char, List<int>> _first = new();

    // 增量（自上次持久化以来）：墓碑 + 新增/改名条目
    private static readonly HashSet<long> _tomb = new();
    private static readonly List<(long Ref, long ParentRef, string Name, bool IsFile)> _delta = new();
    private const int DeltaCompactThreshold = 200_000;   // 增量太大就重建+重存

    private static readonly object Sync = new();
    private static volatile bool _building;
    private static volatile bool _ready;

    public static bool IsReady { get { lock (Sync) return _ready; } }

    /// <summary>索引落盘位置（和 config 同级，便携）。</summary>
    public static string IndexDir => Path.Combine(AppContext.BaseDirectory, "index");
    private static string IndexPath => Path.Combine(IndexDir, "usn.idx");

    // ==================== P/Invoke ====================

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_ENUM_DATA_V0
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct READ_USN_JOURNAL_DATA_V0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalID;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint returned, IntPtr ov);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private const uint GENERIC_READ = 0x80000000;
    private const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
    private const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
    private const int BufSize = 1 << 20;

    private const uint USN_REASON_FILE_CREATE = 0x00000100;
    private const uint USN_REASON_FILE_DELETE = 0x00000200;
    private const uint USN_REASON_RENAME_OLD_NAME = 0x00001000;
    private const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;

    // ==================== 构建 ====================

    /// <summary>后台建索引：优先读盘 + 增量，失败再全量枚举。</summary>
    public static void EnsureBuilding(IEnumerable<string> roots)
    {
        if (_building || _ready) return;

        var vols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in roots)
        {
            var root = Environment.ExpandEnvironmentVariables(raw).Trim();
            if (root.Length < 2 || root[1] != ':') continue;
            vols.Add(@"\\.\" + root[0] + ":");
        }
        if (vols.Count == 0) return;

        lock (Sync)
        {
            if (_building || _ready) return;
            _building = true;
        }

        var volList = vols.ToArray();
        var t = new Thread(() =>
        {
            try
            {
                bool loaded = false;
                try { loaded = TryLoadFromDisk(volList); }
                catch (Exception ex) { DebugLog.Write("[USN] 读档失败，改为全量重建: " + ex.Message); loaded = false; }

                if (!loaded)
                {
                    BuildFull(volList);
                    TryApplyJournalDelta();     // 刚建的索引游标就是现在，通常没有增量
                    TrySaveToDisk();
                }
                else
                {
                    TryApplyJournalDelta();     // 补齐上次启动以来的变化
                    if (_delta.Count > DeltaCompactThreshold)
                    {
                        DebugLog.Write($"[USN] 增量过大（{_delta.Count}），重建索引");
                        BuildFull(volList);
                        TrySaveToDisk();
                    }
                }

                RebuildBuckets();
                lock (Sync) { _ready = true; _building = false; }
                DebugLog.Write($"[USN] 就绪：{_count} 条（墓碑 {_tomb.Count}，增量 {_delta.Count}），内存结构为紧凑数组");
            }
            catch (Exception ex)
            {
                DebugLog.Write("[USN] 构建失败: " + ex.Message);
                lock (Sync) _building = false;
            }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "DeskBuddyUsnIndex" };
        t.Start();
    }

    /// <summary>全量枚举（老路径）。结果直接写进紧凑数组。</summary>
    private static void BuildFull(string[] volumes)
    {
        long sw = Environment.TickCount64;
        var nameBuf = new StringBuilder(8 << 20);

        var tmpRef = new List<long>(1 << 20);
        var tmpParent = new List<long>(1 << 20);
        var tmpOff = new List<int>(1 << 20);
        var tmpLen = new List<ushort>(1 << 20);
        var tmpVol = new List<byte>(1 << 20);
        var tmpName = new List<string>(1 << 20);
        var tmpIsFile = new List<bool>(1 << 20);
        var vols = new List<Volume>();

        for (int vi = 0; vi < volumes.Length; vi++)
        {
            var vol = volumes[vi];
            string drive = vol.Substring(4, 2);
            var vinfo = new Volume { Drive = drive, JournalId = ReadJournalId(vol), NextUsn = ReadNextUsn(vol) };
            vols.Add(vinfo);

            IntPtr h = CreateFile(vol, GENERIC_READ, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (h == new IntPtr(-1))
            {
                int err = Marshal.GetLastWin32Error();
                DebugLog.Write($"[USN] 打开卷失败 {vol} err={err}" + (err == 5 ? "（需要管理员权限）" : ""));
                continue;
            }

            ulong startRef = 0;
            IntPtr inBuf = Marshal.AllocHGlobal(24);
            IntPtr outBuf = Marshal.AllocHGlobal(BufSize);
            try
            {
                while (true)
                {
                    var input = new MFT_ENUM_DATA_V0 { StartFileReferenceNumber = startRef, LowUsn = 0, HighUsn = long.MaxValue };
                    Marshal.StructureToPtr(input, inBuf, false);
                    if (!DeviceIoControl(h, FSCTL_ENUM_USN_DATA, inBuf, 24, outBuf, BufSize, out uint returned, IntPtr.Zero) || returned == 0) break;

                    ulong next = (ulong)Marshal.ReadInt64(outBuf, 0);
                    if (next <= startRef) break;      // 不前进说明枚举结束/异常，防死循环
                    startRef = next;

                    int off = 8;
                    while (off + 60 <= (int)returned)
                    {
                        int recLen = Marshal.ReadInt32(outBuf, off);
                        if (recLen < 40 || off + recLen > (int)returned) break;
                        short major = Marshal.ReadInt16(outBuf, off + 4);
                        if (major != 2 && major != 3) { off += 8; continue; }

                        long fr = Marshal.ReadInt64(outBuf, off + 8);
                        long pr = Marshal.ReadInt64(outBuf, off + 16);
                        short attrs = (short)Marshal.ReadInt32(outBuf, off + 52);
                        short nLen = Marshal.ReadInt16(outBuf, off + 56);
                        short nOff = Marshal.ReadInt16(outBuf, off + 58);

                        // 卷根先判：parent 指向自己或 0，且名字长度为 0
                        if (fr == 5 || pr == fr || pr == 0)
                        {
                            tmpRef.Add(fr); tmpParent.Add(-1); tmpOff.Add(0); tmpLen.Add(0);
                            tmpVol.Add((byte)vi); tmpName.Add(drive); tmpIsFile.Add(false);
                        }
                        else if (nLen > 0 && nLen < 2000 && off + nOff + nLen <= (int)returned)
                        {
                            // ⚠目录也必须收：文件的 parent 指向目录，目录不收就拼不出完整路径
                            string name = Marshal.PtrToStringUni(outBuf + off + nOff, nLen / 2) ?? "";
                            if (name.Length == 0) { off += recLen; continue; }
                            tmpRef.Add(fr); tmpParent.Add(pr); tmpOff.Add(0); tmpLen.Add((ushort)name.Length);
                            tmpVol.Add((byte)vi); tmpName.Add(name);
                            tmpIsFile.Add((attrs & 0x10) == 0);
                        }
                        off += recLen;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(inBuf); Marshal.FreeHGlobal(outBuf); CloseHandle(h); }
        }

        // 一次性拼名字缓冲 + 解析 parent（ref→slot）
        var byRef = new Dictionary<long, int>(tmpRef.Count);
        for (int i = 0; i < tmpRef.Count; i++) byRef[tmpRef[i]] = i;

        int n = tmpRef.Count;
        _ref = new long[n]; _parent = new int[n]; _nameOff = new int[n];
        _nameLen = new ushort[n]; _vol = new byte[n]; _alive = new bool[n]; _isFile = new bool[n];
        _nameBuf = new char[tmpName.Sum(s => s.Length)];
        _rootDrive = new Dictionary<int, string>();
        int pos = 0;
        for (int i = 0; i < n; i++)
        {
            _ref[i] = tmpRef[i];
            _nameOff[i] = pos;
            _nameLen[i] = tmpLen[i];
            _vol[i] = tmpVol[i];
            _isFile[i] = tmpIsFile[i];
            _alive[i] = true;
            var s = tmpName[i];
            s.CopyTo(0, _nameBuf, pos, s.Length);
            pos += s.Length;
            _parent[i] = tmpParent[i] < 0 ? -1 : (byRef.TryGetValue(tmpParent[i], out var ps) ? ps : -1);
            if (tmpParent[i] < 0) _rootDrive[i] = s;
        }
        _count = n;
        _vols = vols;
        _tomb.Clear(); _delta.Clear();
        BuildRefLookup();
        DebugLog.Write($"[USN] 全量枚举完成：{_count} 条，用时 {(Environment.TickCount64 - sw) / 1000.0:F1}s");
    }

    // ==================== 查询 ====================

    private static void RebuildBuckets()
    {
        _prefix2 = new Dictionary<string, List<int>>(1024, StringComparer.Ordinal);
        _first = new Dictionary<char, List<int>>(256);
        for (int i = 0; i < _count; i++)
        {
            if (!_alive[i] || _nameLen[i] == 0 || !_isFile[i]) continue;
            var span = _nameBuf.AsSpan(_nameOff[i], _nameLen[i]);
            char c0 = char.ToLowerInvariant(span[0]);
            if (!_first.TryGetValue(c0, out var l0)) { l0 = new List<int>(512); _first[c0] = l0; }
            l0.Add(i);
            if (span.Length >= 2)
            {
                var key = string.Create(2, (c0, char.ToLowerInvariant(span[1])), (dst, st) => { dst[0] = st.Item1; dst[1] = st.Item2; });
                if (!_prefix2.TryGetValue(key, out var l2)) { l2 = new List<int>(128); _prefix2[key] = l2; }
                l2.Add(i);
            }
        }
    }

    /// <summary>按文件名子串搜索（忽略大小写），最多 limit 条，返回完整路径。</summary>
    public static List<string> Search(string query, string[] rootFilters, int limit = 12)
    {
        var results = new List<string>();
        lock (Sync)
        {
            if (!_ready || string.IsNullOrWhiteSpace(query)) return results;
            var q = query.Trim();
            var qs = q.AsSpan();

            if (q.Length >= 2)
            {
                var p2 = _prefix2.TryGetValue(q.Substring(0, 2).ToLowerInvariant(), out var tmp2) ? tmp2 : null;
                ScanBucket(p2, qs, rootFilters, limit, results);
            }

            if (results.Count < limit)
                ScanBucket(_first.TryGetValue(char.ToLowerInvariant(q[0]), out var f1) ? f1 : null, qs, rootFilters, limit, results);

            // 增量部分（未落盘的新增/改名）
            if (results.Count < limit && _delta.Count > 0)
            {
                for (int i = _delta.Count - 1; i >= 0 && results.Count < limit; i--)
                {
                    var d = _delta[i];
                    if (!d.IsFile || _tomb.Contains(d.Ref)) continue;
                    if (d.Name.AsSpan().IndexOf(qs, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var path = ResolveDeltaPath(d);
                    if (path != null && MatchesRoot(path, rootFilters) && !results.Contains(path, StringComparer.OrdinalIgnoreCase))
                        results.Add(path);
                }
            }
        }
        return results;
    }

    private static void ScanBucket(List<int> bucket, ReadOnlySpan<char> q, string[] rootFilters, int limit, List<string> results)
    {
        if (bucket == null) return;
        int matched = 0, nullPath = 0, filtered = 0, dead = 0;
        for (int k = bucket.Count - 1; k >= 0 && results.Count < limit; k--)
        {
            int slot = bucket[k];
            if (!_alive[slot]) { dead++; continue; }
            if (_tomb.Count > 0 && _tomb.Contains(_ref[slot])) continue;
            ReadOnlySpan<char> name = _nameBuf.AsSpan(_nameOff[slot], _nameLen[slot]);
            if (name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            matched++;
            var path = ResolvePath(slot);
            if (path == null) { nullPath++; continue; }
            if (!MatchesRoot(path, rootFilters)) { filtered++; continue; }
            if (!results.Contains(path, StringComparer.OrdinalIgnoreCase)) results.Add(path);
        }
    }

    /// <summary>slot → 完整路径（沿父 slot 上溯；遇到没有名字/没有父的就是卷根，补盘符）。</summary>
    private static string? ResolvePath(int slot)
    {
        var parts = new List<ReadOnlyMemory<char>>(8);
        int cur = slot;
        int guard = 0;
        while (cur >= 0 && cur < _count)
        {
            if (++guard > 256) return null;
            // ★先收自己这一级（卷根本身的名字长度为 0，自然跳过）。
            //   注意： 记录不一定被 USN 枚举出来，所以最顶层真实目录（如 Future）
            //   的 parent 会落空（-1）。此时把它的名字也带上，否则路径会少一段。
            if (_nameLen[cur] > 0)
                parts.Add(new ReadOnlyMemory<char>(_nameBuf, _nameOff[cur], _nameLen[cur]));

            if (_parent[cur] < 0)
            {
                string? drive = null;
                if (_rootDrive.TryGetValue(cur, out var rd)) drive = rd;
                else if (_vol[cur] < _vols.Count) drive = _vols[_vol[cur]].Drive;
                if (drive == null) return null;

                var sb = new StringBuilder(128);
                sb.Append(drive).Append('\\');
                for (int i = parts.Count - 1; i >= 0; i--) sb.Append(parts[i].Span).Append('\\');
                if (parts.Count > 0) sb.Length -= 1;
                return sb.ToString();
            }
            cur = _parent[cur];
        }
        return null;
    }
    /// <summary>增量条目 → 路径（父可能是增量里的新目录）。</summary>
    private static string? ResolveDeltaPath((long Ref, long ParentRef, string Name, bool IsFile) d)
    {
        var parts = new List<string> { d.Name };
        long parentRef = d.ParentRef;
        int guard = 0;
        while (guard++ < 256)
        {
            // 父在 base 里？
            int baseSlot = FindSlotByRef(parentRef);
            if (baseSlot >= 0) { var p = ResolvePath(baseSlot); if (p == null) return null; return p + "\\" + string.Join("\\", parts); }
            // 父在增量里？
            var pv = _delta.Find(x => x.Ref == parentRef && !_tomb.Contains(x.Ref));
            if (pv.Name.Length == 0) return null;
            parts.Add(pv.Name);
            parentRef = pv.ParentRef;
        }
        return null;
    }

    /// <summary>ref → slot（用按 ref 排序的辅助数组二分；5.5M 条约 66MB，换掉线性扫描）。</summary>
    private static long[] _sortedRef = Array.Empty<long>();
    private static int[] _sortedSlot = Array.Empty<int>();

    private static void BuildRefLookup()
    {
        var slots = new int[_count];
        for (int i = 0; i < _count; i++) slots[i] = i;
        Array.Sort(slots, (a, b) => _ref[a].CompareTo(_ref[b]));
        _sortedRef = new long[_count];
        _sortedSlot = slots;
        for (int i = 0; i < _count; i++) _sortedRef[i] = _ref[slots[i]];
    }

    private static int FindSlotByRef(long r)
    {
        int lo = 0, hi = _sortedRef.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            long v = _sortedRef[mid];
            if (v == r) return _sortedSlot[mid];
            if (v < r) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }

    private static bool MatchesRoot(string path, string[] rootFilters)
    {
        if (rootFilters == null || rootFilters.Length == 0) return true;
        foreach (var raw in rootFilters)
        {
            var root = Environment.ExpandEnvironmentVariables(raw).Trim().TrimEnd('\\') + "\\";
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ==================== 增量（USN 日志） ====================

    private static void TryApplyJournalDelta()
    {
        for (int vi = 0; vi < _vols.Count; vi++)
        {
            var v = _vols[vi];
            try { ApplyJournalDelta(v); }
            catch (Exception ex) { DebugLog.Write($"[USN] {v.Drive} 增量失败: {ex.Message}"); }
        }
    }

    private static void ApplyJournalDelta(Volume v)
    {
        if (v.NextUsn <= 0 || v.JournalId == 0) return;
        string volPath = @"\\.\" + v.Drive;
        IntPtr h = CreateFile(volPath, GENERIC_READ, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (h == new IntPtr(-1)) return;
        IntPtr inBuf = Marshal.AllocHGlobal(40);
        IntPtr outBuf = Marshal.AllocHGlobal(BufSize);
        long applied = 0;
        try
        {
            long start = v.NextUsn;
            while (true)
            {
                var input = new READ_USN_JOURNAL_DATA_V0
                {
                    StartUsn = start,
                    ReasonMask = 0xFFFFFFFF,
                    ReturnOnlyOnClose = 0,
                    Timeout = 0,
                    BytesToWaitFor = 0,
                    UsnJournalID = (ulong)v.JournalId,
                };
                Marshal.StructureToPtr(input, inBuf, false);
                if (!DeviceIoControl(h, FSCTL_READ_USN_JOURNAL, inBuf, 40, outBuf, BufSize, out uint returned, IntPtr.Zero) || returned <= 8) break;

                long nextStart = Marshal.ReadInt64(outBuf, 0);
                if (nextStart <= start) break;
                start = nextStart;

                int off = 8;
                while (off + 60 <= (int)returned)
                {
                    int recLen = Marshal.ReadInt32(outBuf, off);
                    if (recLen < 40 || off + recLen > (int)returned) break;
                    short major = Marshal.ReadInt16(outBuf, off + 4);
                    if (major != 2 && major != 3) { off += 8; continue; }

                    long fr = Marshal.ReadInt64(outBuf, off + 8);
                    long pr = Marshal.ReadInt64(outBuf, off + 16);
                    uint reason = (uint)Marshal.ReadInt32(outBuf, off + 40);
                    short attrs = (short)Marshal.ReadInt32(outBuf, off + 52);
                    short nLen = Marshal.ReadInt16(outBuf, off + 56);
                    short nOff = Marshal.ReadInt16(outBuf, off + 58);

                    if ((reason & USN_REASON_FILE_DELETE) != 0 || (reason & USN_REASON_RENAME_OLD_NAME) != 0)
                    {
                        _tomb.Add(fr);
                        applied++;
                    }
                    else if ((reason & (USN_REASON_FILE_CREATE | USN_REASON_RENAME_NEW_NAME)) != 0
                             && nLen > 0 && nLen < 2000 && off + nOff + nLen <= (int)returned)
                    {
                        // 目录也要收：新目录下的新文件要靠它拼路径（目录只做路径中转，不参与匹配）
                        string name = Marshal.PtrToStringUni(outBuf + off + nOff, nLen / 2) ?? "";
                        if (name.Length > 0)
                        {
                            _tomb.Remove(fr);
                            _delta.Add((fr, pr, name, (attrs & 0x10) == 0));
                            applied++;
                        }
                    }
                    off += recLen;
                }
            }
            v.NextUsn = start;
        }
        finally { Marshal.FreeHGlobal(inBuf); Marshal.FreeHGlobal(outBuf); CloseHandle(h); }
        if (applied > 0) DebugLog.Write($"[USN] {v.Drive} 增量应用 {applied} 条");
    }

    private static long ReadJournalId(string volPath)
    {
        try
        {
            IntPtr h = CreateFile(volPath, GENERIC_READ, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (h == new IntPtr(-1)) return 0;
            try
            {
                IntPtr buf = Marshal.AllocHGlobal(64);
                try
                {
                    // USN_JOURNAL_DATA_V0: [0]=UsnJournalID [8]=FirstUsn [16]=NextUsn
                    if (DeviceIoControl(h, 0x000900F4, IntPtr.Zero, 0, buf, 64, out _, IntPtr.Zero))
                        return Marshal.ReadInt64(buf, 0);    // UsnJournalID 在偏移 0
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(h); }
        }
        catch { }
        return 0;
    }

    private static long ReadNextUsn(string volPath)
    {
        try
        {
            IntPtr h = CreateFile(volPath, GENERIC_READ, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (h == new IntPtr(-1)) return 0;
            try
            {
                IntPtr buf = Marshal.AllocHGlobal(64);
                try
                {
                    if (DeviceIoControl(h, 0x000900F4, IntPtr.Zero, 0, buf, 64, out _, IntPtr.Zero))
                        return Marshal.ReadInt64(buf, 16);   // NextUsn 在偏移 16
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(h); }
        }
        catch { }
        return 0;
    }

    // ==================== 持久化 ====================

    private const string Magic = "DBUSN2\0\0";
    private const int Version = 3;

    private static void TrySaveToDisk()
    {
        try
        {
            Directory.CreateDirectory(IndexDir);
            var tmp = IndexPath + ".tmp";
            using (var fs = File.Create(tmp))
            using (var w = new BinaryWriter(fs, Encoding.UTF8))
            {
                w.Write(Encoding.ASCII.GetBytes(Magic), 0, 8);
                w.Write(Version);
                w.Write(_vols.Count);
                foreach (var v in _vols) { w.Write(v.Drive); w.Write(v.JournalId); w.Write(v.NextUsn); }

                w.Write(_count);
                w.Write(_nameBuf.Length);
                for (int i = 0; i < _count; i++) w.Write(_ref[i]);
                for (int i = 0; i < _count; i++) w.Write(_parent[i]);
                for (int i = 0; i < _count; i++) w.Write(_nameOff[i]);
                for (int i = 0; i < _count; i++) w.Write(_nameLen[i]);
                for (int i = 0; i < _count; i++) w.Write(_vol[i]);
                for (int i = 0; i < _count; i++) w.Write(_isFile[i]);
                w.Write(_nameBuf, 0, _nameBuf.Length);

                w.Write(_rootDrive.Count);
                foreach (var kv in _rootDrive) { w.Write(kv.Key); w.Write(kv.Value); }
            }
            if (File.Exists(IndexPath)) File.Delete(IndexPath);
            File.Move(tmp, IndexPath);
            DebugLog.Write($"[USN] 索引已落盘：{_count} 条，{new FileInfo(IndexPath).Length / 1024 / 1024} MB");
        }
        catch (Exception ex) { DebugLog.Write("[USN] 落盘失败: " + ex.Message); }
    }

    /// <summary>读盘；卷集合/版本不符或数据异常返回 false（由调用方回退全量重建）。</summary>
    private static bool TryLoadFromDisk(string[] expectVols)
    {
        if (!File.Exists(IndexPath)) return false;
        long sw = Environment.TickCount64;
        using var fs = File.OpenRead(IndexPath);
        using var r = new BinaryReader(fs, Encoding.UTF8);

        var magic = Encoding.ASCII.GetString(r.ReadBytes(8));
        if (magic != Magic) { DebugLog.Write("[USN] 索引版本不符，重建"); return false; }
        if (r.ReadInt32() != Version) { DebugLog.Write("[USN] 索引版本号不符，重建"); return false; }

        int volCount = r.ReadInt32();
        var vols = new List<Volume>();
        for (int i = 0; i < volCount; i++) vols.Add(new Volume { Drive = r.ReadString(), JournalId = r.ReadInt64(), NextUsn = r.ReadInt64() });
        if (vols.Count != expectVols.Length) { DebugLog.Write("[USN] 卷数量变化，重建"); return false; }
        for (int i = 0; i < vols.Count; i++)
        {
            string expectDrive = expectVols[i].Substring(4, 2);
            if (!string.Equals(vols[i].Drive, expectDrive, StringComparison.OrdinalIgnoreCase))
            {
                DebugLog.Write($"[USN] 卷不匹配（索引 {vols[i].Drive} / 当前 {expectDrive}），重建");
                return false;
            }
            long jid = ReadJournalId(expectVols[i]);
            if (jid != 0 && vols[i].JournalId != 0 && jid != vols[i].JournalId)
            {
                DebugLog.Write($"[USN] {expectDrive} 日志已重建，重建索引");
                return false;
            }
        }

        int count = r.ReadInt32();
        int nameBufLen = r.ReadInt32();
        if (count <= 0 || nameBufLen <= 0 || count > 20_000_000) { DebugLog.Write("[USN] 索引数据异常，重建"); return false; }

        var refs = new long[count]; var par = new int[count]; var offs = new int[count];
        var lens = new ushort[count]; var vol = new byte[count]; var alive = new bool[count];
        for (int i = 0; i < count; i++) refs[i] = r.ReadInt64();
        for (int i = 0; i < count; i++) par[i] = r.ReadInt32();
        for (int i = 0; i < count; i++) offs[i] = r.ReadInt32();
        for (int i = 0; i < count; i++) lens[i] = r.ReadUInt16();
        for (int i = 0; i < count; i++) vol[i] = r.ReadByte();
        var isFile = new bool[count];
        for (int i = 0; i < count; i++) isFile[i] = r.ReadBoolean();
        Array.Fill(alive, true);          // ★读回来的都是活的（墓碑由增量另行维护）
        var nb = r.ReadChars(nameBufLen);
        int rootCount = r.ReadInt32();
        var roots = new Dictionary<int, string>();
        for (int i = 0; i < rootCount; i++) roots[r.ReadInt32()] = r.ReadString();

        _ref = refs; _parent = par; _nameOff = offs; _nameLen = lens; _vol = vol; _alive = alive;
        _nameBuf = nb; _count = count; _rootDrive = roots; _vols = vols; _isFile = isFile;
        _tomb.Clear(); _delta.Clear();
        BuildRefLookup();
        DebugLog.Write($"[USN] 读盘完成：{_count} 条，用时 {(Environment.TickCount64 - sw) / 1000.0:F1}s");
        return true;
    }
}
