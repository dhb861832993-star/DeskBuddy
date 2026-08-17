using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskBuddy.Services;

/// <summary>
/// DeskBuddy 自有磁盘索引引擎（Listary DiskSearch 同技术路线）：
/// 通过 NTFS USN 日志（Change Journal）枚举卷内全部文件名，构建内存索引，
/// 完全不依赖 Windows Search。支持千万级文件、秒级构建、毫秒级搜索。
/// </summary>
public static class UsnIndex
{
    // ==================== 状态 ====================

    private static readonly object Sync = new();
    private static Dictionary<long, long> _parent = new();       // fileRef -> parentRef（用于还原路径）
    private static Dictionary<long, string> _names = new();      // fileRef -> 文件名
    private static Dictionary<char, List<long>> _buckets = new(); // 首字符 -> 文件引用（子串匹配兜底）
    private static Dictionary<string, List<long>> _prefixBuckets = new(); // 前2字符 -> 文件引用（前缀快速路径）
    private static Dictionary<long, string> _dirPathCache = new(); // 目录 ref -> 完整路径（加速还原）
    private static Dictionary<long, string> _rootDrives = new();  // 卷根 ref -> 盘符（如 "E:"），还原路径补盘符用
    private static string[] _volumes = Array.Empty<string>();
    private static volatile bool _building;
    private static volatile bool _ready;
    private static string _lastQuery = "";
    private static List<string> _lastResults = new();
    private static string[] _lastRoots = Array.Empty<string>();

    public static bool IsReady { get { lock (Sync) return _ready; } }

    // ==================== P/Invoke（已在本机验证） ====================

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_ENUM_DATA_V0 { public ulong StartFileReferenceNumber; public long LowUsn; public long HighUsn; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint returned, IntPtr ov);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private const uint GENERIC_READ = 0x80000000;
    private const uint FSCTL_ENUM_USN_DATA = 0x000900B3; // 本机实测的枚举控制码
    private const int BufSize = 1 << 20;

    // ==================== 构建（后台） ====================

    /// <summary>在后台构建索引（每个搜索根目录所在卷枚举一次；同一卷只建一次）。</summary>
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
        var thread = new Thread(() =>
        {
            try
            {
                Build(volList);
                lock (Sync)
                {
                    _volumes = volList;
                    _ready = true;
                    _building = false;
                }
                DebugLog.Write($"UsnIndex.Build: ready, entries={_names.Count}");
            }
            catch (Exception ex)
            {
                DebugLog.Write($"UsnIndex.Build: 失败 {ex.Message}");
                lock (Sync) _building = false;
            }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "DeskBuddyUsnIndex" };
        thread.Start();
    }

    private static void Build(string[] volumes)
    {
        var parent = new Dictionary<long, long>();
        var names = new Dictionary<long, string>();
        var buckets = new Dictionary<char, List<long>>();
        var prefixBuckets = new Dictionary<string, List<long>>();
        var rootDrives = new Dictionary<long, string>();
        long sw = Environment.TickCount64;

        foreach (var vol in volumes)
        {
            string drive = vol.Substring(4, 2); // "\\.\E:" -> "E:"
            IntPtr h = CreateFile(vol, GENERIC_READ, 0x1 | 0x2, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (h == new IntPtr(-1)) continue;

            ulong startRef = 0; long highUsn = long.MaxValue;
            while (true)
            {
                var input = new MFT_ENUM_DATA_V0 { StartFileReferenceNumber = startRef, LowUsn = 0, HighUsn = highUsn };
                IntPtr inBuf = Marshal.AllocHGlobal(24);
                Marshal.StructureToPtr(input, inBuf, false);
                IntPtr outBuf = Marshal.AllocHGlobal(BufSize);
                uint returned = 0;
                bool ok = DeviceIoControl(h, FSCTL_ENUM_USN_DATA, inBuf, 24, outBuf, BufSize, out returned, IntPtr.Zero);
                Marshal.FreeHGlobal(inBuf);
                if (!ok || returned == 0) { Marshal.FreeHGlobal(outBuf); break; }

                // 跳过前 8 字节前缀，从偏移 8 开始解析 V2 记录
                int off = 8;
                while (off + 60 <= (int)returned)
                {
                    int recordLength = Marshal.ReadInt32(outBuf, off);
                    if (recordLength < 40 || off + recordLength > (int)returned) break;
                    short major = Marshal.ReadInt16(outBuf, off + 4);
                    if (major != 2 && major != 3) { off += 8; continue; }
                    long fileRef = Marshal.ReadInt64(outBuf, off + 8);
                    long parentRef = Marshal.ReadInt64(outBuf, off + 16);
                    short attrs = (short)Marshal.ReadInt32(outBuf, off + 52);
                    short nameLen = Marshal.ReadInt16(outBuf, off + 56);
                    short nameOff = Marshal.ReadInt16(outBuf, off + 58);
                    if (nameLen > 0 && nameLen < 2000 && off + nameOff + nameLen <= (int)returned)
                    {
                        string name = Marshal.PtrToStringUni(outBuf + off + nameOff, nameLen / 2) ?? "";
                        if (name.Length > 0)
                        {
                            names[fileRef] = name;
                            parent[fileRef] = parentRef;
                            // 只有普通文件（非目录）进入搜索桶
                            if ((attrs & 0x10) == 0)
                            {
                                var c = char.ToLowerInvariant(name[0]);
                                if (!buckets.TryGetValue(c, out var list))
                                {
                                    list = new List<long>(1024);
                                    buckets[c] = list;
                                }
                                list.Add(fileRef);
                                // 前 2 字符前缀桶（小桶，前缀匹配快上千倍）
                                var pkey = name.Length >= 2
                                    ? char.ToLowerInvariant(name[0]).ToString() + char.ToLowerInvariant(name[1])
                                    : c.ToString();
                                if (!prefixBuckets.TryGetValue(pkey, out var plist))
                                {
                                    plist = new List<long>(128);
                                    prefixBuckets[pkey] = plist;
                                }
                                plist.Add(fileRef);
                            }
                            startRef = (ulong)fileRef;
                        }
                        else
                        {
                            // 空名字 = 卷根目录（$Root），记录其引用以补盘符
                            rootDrives[fileRef] = drive;
                        }
                    }
                    off += recordLength;
                }
                Marshal.FreeHGlobal(outBuf);
            }
            CloseHandle(h);
        }

        lock (Sync)
        {
            _parent = parent;
            _names = names;
            _buckets = buckets;
            _prefixBuckets = prefixBuckets;
            _rootDrives = rootDrives;
            _dirPathCache = new Dictionary<long, string>();
            _lastQuery = "";
            _lastResults = new List<string>();
        }
        DebugLog.Write($"UsnIndex.Build: {names.Count} 条, 用时 {(Environment.TickCount64 - sw) / 1000.0:F1}s");
    }

    // ==================== 搜索 ====================

    /// <summary>按文件名子串搜索（忽略大小写），最多 limit 条，返回完整路径。</summary>
    public static List<string> Search(string query, string[] rootFilters, int limit = 12)
    {
        lock (Sync)
        {
            if (!_ready || string.IsNullOrWhiteSpace(query)) return new List<string>();
            var results = new List<string>();

            var q = query.Trim();
            var triedIncremental = false;

            // 搜索范围变了 → 上次的增量结果作废（防止旧范围文件泄漏到新范围结果）
            if (!SameRoots(_lastRoots, rootFilters))
            {
                _lastQuery = "";
                _lastResults = new List<string>();
                _lastRoots = rootFilters;
            }

            // 增量过滤：新查询以旧查询开头时，直接过滤上次结果（仍按当前范围过滤）
            if (_lastResults.Count > 0 && _lastQuery.Length > 0 && q.StartsWith(_lastQuery, StringComparison.OrdinalIgnoreCase) && q.Length > _lastQuery.Length)
            {
                triedIncremental = true;
                foreach (var path in _lastResults)
                {
                    if (results.Count >= limit) break;
                    if (MatchesRoot(path, rootFilters) && Path.GetFileName(path).Contains(q, StringComparison.OrdinalIgnoreCase))
                        results.Add(path);
                }
            }
            if (!triedIncremental || results.Count == 0)
            {
                results = new List<string>();
                var c = char.ToLowerInvariant(q[0]);
                var seen = new HashSet<long>();

                // 快速路径：前 2 字符小桶（绝大多数查询毫秒内命中）
                if (q.Length >= 2)
                {
                    var pkey = q.Substring(0, 2).ToLowerInvariant();
                    if (_prefixBuckets.TryGetValue(pkey, out var plist))
                    {
                        foreach (var ref_ in plist)
                        {
                            if (results.Count >= limit) break;
                            var name = _names[ref_];
                            if (name.Contains(q, StringComparison.OrdinalIgnoreCase))
                            {
                                var path = ResolvePath(ref_);
                                if (path != null && MatchesRoot(path, rootFilters))
                                {
                                    seen.Add(ref_);
                                    results.Add(path);
                                }
                            }
                        }
                    }
                }

                // 兜底：前缀桶不够时扫首字符桶（子串匹配，如查询不在开头出现）
                if (results.Count < limit && _buckets.TryGetValue(c, out var list))
                {
                    foreach (var ref_ in list)
                    {
                        if (results.Count >= limit) break;
                        if (seen.Contains(ref_)) continue;
                        var name = _names[ref_];
                        if (name.Contains(q, StringComparison.OrdinalIgnoreCase))
                        {
                            var path = ResolvePath(ref_);
                            if (path != null && MatchesRoot(path, rootFilters)) results.Add(path);
                        }
                    }
                }
            }
            _lastQuery = q;
            _lastResults = results;
            return results;
        }
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

    private static bool SameRoots(string[] a, string[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (!string.Equals(Environment.ExpandEnvironmentVariables(a[i]).Trim().TrimEnd('\\'),
                               Environment.ExpandEnvironmentVariables(b[i]).Trim().TrimEnd('\\'),
                               StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>通过父引用链还原完整路径（目录路径有缓存；卷根补盘符）。</summary>
    private static string? ResolvePath(long fileRef)
    {
        if (_dirPathCache.TryGetValue(fileRef, out var cached)) return cached;
        var parts = new List<string>();
        string? drive = null;
        long cur = fileRef;
        var guard = 0;
        while (guard++ < 128)
        {
            if (_rootDrives.TryGetValue(cur, out var d)) { drive = d; break; }
            if (!_names.TryGetValue(cur, out var nm)) break;
            parts.Add(nm);
            if (!_parent.TryGetValue(cur, out var p)) break;
            if (p == cur) break;
            cur = p;
            if (_dirPathCache.TryGetValue(cur, out var dirPath))
            {
                parts.Reverse();
                var full = dirPath + string.Join("\\", parts);
                _dirPathCache[fileRef] = full;
                return full;
            }
        }
        parts.Reverse();
        var path = drive != null ? drive + "\\" + string.Join("\\", parts) : string.Join("\\", parts);
        if (path.Length > 0) _dirPathCache[fileRef] = path;
        return path;
    }
}
