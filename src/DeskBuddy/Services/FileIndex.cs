using System.IO;

namespace DeskBuddy.Services;

/// <summary>
/// 文件名内存索引：后台把搜索范围的文件名一次性装入内存（低优先级线程），
/// 之后每次搜索都在内存里做子串匹配（毫秒级）。
/// 通过 FileSystemWatcher 监听搜索范围，文件新建/删除/改名后防抖做【增量更新】——
/// 只增删对应条目，不做全盘重建，避免拖慢电脑；全量重建仅发生在启动/范围变更/监听异常时。
/// </summary>
public static class FileIndex
{
    private static readonly object Sync = new();
    private static readonly object WatcherSync = new();
    private static readonly List<(string Name, string Path)> Entries = new();
    private static readonly Dictionary<string, int> PathIndex = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<FileSystemWatcher> Watchers = new();
    private static readonly HashSet<string> PendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _builtAt = DateTime.MinValue;
    private static string[] _roots = Array.Empty<string>();
    private static string[] _watchedRoots = Array.Empty<string>();
    private static CancellationTokenSource? _buildCts;
    private static CancellationTokenSource? _debounceCts;
    private static int _buildVersion;

    /// <summary>索引是否已有数据（可提供毫秒级搜索）。</summary>
    public static bool IsReady
    {
        get { lock (Sync) return Entries.Count > 0; }
    }

    /// <summary>确保索引可用：新鲜且范围未变则复用；否则低优先级后台重建。同时启动实时监听。</summary>
    public static void EnsureBuilding(string[] roots)
    {
        lock (Sync)
        {
            StartWatchers(roots);
            if (_buildCts is { IsCancellationRequested: false }) return; // 正在构建
            if (SameRoots(roots) && (DateTime.UtcNow - _builtAt).TotalMinutes < 3) return; // 足够新
            StartBuild(roots);
        }
    }

    /// <summary>在索引中按文件名子串匹配（忽略大小写），最多 limit 条。</summary>
    public static List<(string Name, string Path)> Search(string query, int limit = 12)
    {
        var results = new List<(string Name, string Path)>();
        lock (Sync)
        {
            foreach (var e in Entries)
            {
                if (results.Count >= limit) break;
                if (e.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(e);
                }
            }
        }
        return results;
    }

    // ==================== 全量构建（低优先级后台） ====================

    private static void StartBuild(string[] roots)
    {
        _buildCts?.Dispose();
        _buildCts = new CancellationTokenSource();
        var ct = _buildCts.Token;
        var version = ++_buildVersion;
        var thread = new Thread(() =>
        {
            var found = FileSearcher.IndexAll(roots, ct);
            lock (Sync)
            {
                if (version == _buildVersion)
                {
                    Entries.Clear();
                    Entries.AddRange(found);
                    RebuildPathIndex();
                    _builtAt = DateTime.UtcNow;
                    _roots = roots;
                }
            }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "DeskBuddyFileIndex" };
        thread.Start();
    }

    private static void RebuildPathIndex()
    {
        PathIndex.Clear();
        for (var i = 0; i < Entries.Count; i++)
        {
            PathIndex[Entries[i].Path] = i;
        }
    }

    private static bool SameRoots(string[] roots)
    {
        if (_roots.Length != roots.Length) return false;
        for (var i = 0; i < roots.Length; i++)
        {
            if (!string.Equals(_roots[i], roots[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    // ==================== 实时监听 + 增量更新 ====================

    private static void StartWatchers(string[] roots)
    {
        lock (WatcherSync)
        {
            if (SameWatched(roots)) return;
            foreach (var w in Watchers)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            }
            Watchers.Clear();
            _watchedRoots = roots;

            foreach (var raw in roots)
            {
                var root = Environment.ExpandEnvironmentVariables(raw).Trim();
                if (root.Length == 0 || !Directory.Exists(root)) continue;
                try
                {
                    var w = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        Filter = "*",
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                    };
                    w.Created += OnChanged;
                    w.Deleted += OnChanged;
                    w.Renamed += OnRenamed;
                    w.Error += (_, _) => OnWatcherError(); // 缓冲区溢出等：安全兜底重建
                    w.EnableRaisingEvents = true;
                    Watchers.Add(w);
                }
                catch { /* 单个根监听失败不影响搜索 */ }
            }
        }
    }

    private static bool SameWatched(string[] roots)
    {
        if (_watchedRoots.Length != roots.Length) return false;
        for (var i = 0; i < roots.Length; i++)
        {
            if (!string.Equals(_watchedRoots[i], roots[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static void OnChanged(object sender, FileSystemEventArgs e) => NoteChange(e.FullPath);

    private static void OnRenamed(object sender, RenamedEventArgs e)
    {
        NoteChange(e.OldFullPath);
        NoteChange(e.FullPath);
    }

    private static void NoteChange(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (Directory.Exists(path)) return; // 只关心文件变化
        lock (Sync)
        {
            PendingChanges.Add(path);
        }
        DebugLog.Write($"FileIndex.NoteChange: {path}");
        ScheduleApply();
    }

    /// <summary>防抖 1.5 秒后把待处理变化增量应用到索引（只增删对应条目，不整盘重建）。</summary>
    private static void ScheduleApply()
    {
        lock (Sync)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var ct = _debounceCts.Token;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(1500, ct); } catch { return; }
                if (ct.IsCancellationRequested) return;
                ApplyPendingChanges();
            });
        }
    }

    private static void ApplyPendingChanges()
    {
        List<string> pending;
        lock (Sync)
        {
            pending = PendingChanges.ToList();
            PendingChanges.Clear();
        }
        DebugLog.Write($"FileIndex.ApplyPendingChanges: pending={pending.Count} [{string.Join(",", pending)}]");
        if (pending.Count == 0) return;

        lock (Sync)
        {
            // 1) 已不存在的路径 → 从索引移除（覆盖删除/改名旧路径）
            var removals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pending)
            {
                if (!File.Exists(p)) removals.Add(p);
            }
            if (removals.Count > 0 && PathIndex.Count > 0)
            {
                DebugLog.Write($"FileIndex: removing {removals.Count} entries (was {Entries.Count})");
                var kept = Entries.Where(e => !removals.Contains(e.Path)).ToList();
                Entries.Clear();
                Entries.AddRange(kept);
                RebuildPathIndex();
            }

            // 2) 存在且未索引的路径 → 增量加入
            foreach (var p in pending)
            {
                if (!File.Exists(p)) continue;
                if (PathIndex.ContainsKey(p)) continue; // 已索引
                if (!ShouldIndexPath(p)) continue;
                Entries.Add((Path.GetFileName(p), p));
                PathIndex[p] = Entries.Count - 1;
            }
        }
    }

    /// <summary>监听异常（如缓冲区溢出）时安全兜底：全量重建（低优先级）。</summary>
    private static void OnWatcherError()
    {
        lock (Sync)
        {
            if (_buildCts is { IsCancellationRequested: false }) return;
            StartBuild((string[])_roots.Clone());
        }
    }

    private static bool ShouldIndexPath(string path)
    {
        foreach (var raw in _roots)
        {
            var root = Environment.ExpandEnvironmentVariables(raw).Trim().TrimEnd('\\') + "\\";
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            return FileSearcher.ShouldIndexPath(path, root);
        }
        return false;
    }
}
