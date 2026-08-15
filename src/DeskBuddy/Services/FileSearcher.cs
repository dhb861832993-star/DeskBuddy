using System.IO;

namespace DeskBuddy.Services;

/// <summary>
/// 按配置的搜索范围（目录路径）遍历文件：支持实时搜索（带目录列表缓存）和全量建索引。
/// 自动跳过隐藏/系统/构建产物/链接目录，限制深度，支持取消。
/// </summary>
public static class FileSearcher
{
    private const int DefaultMaxDepth = 5;
    private const int DefaultMaxResults = 12;
    private const int IndexEntryCap = 300000; // 索引条目上限，防止极端目录撑爆内存

    /// <summary>目录列表缓存：连续输入时复用最近枚举结果，避免反复扫盘。</summary>
    private static readonly Dictionary<string, (string[] Files, string[] Dirs, DateTime Time)> DirCache = new();
    private const int CacheTtlSeconds = 20;

    private static (string[] Files, string[] Dirs) GetListings(string dir)
    {
        lock (DirCache)
        {
            if (DirCache.TryGetValue(dir, out var hit) &&
                (DateTime.UtcNow - hit.Time).TotalSeconds < CacheTtlSeconds)
            {
                return (hit.Files, hit.Dirs);
            }
        }
        string[] files, dirs;
        try
        {
            files = Directory.GetFiles(dir);
            dirs = Directory.GetDirectories(dir);
        }
        catch
        {
            return (Array.Empty<string>(), Array.Empty<string>());
        }
        lock (DirCache)
        {
            DirCache[dir] = (files, dirs, DateTime.UtcNow);
        }
        return (files, dirs);
    }

    /// <summary>实时搜索：返回匹配的文件（名称 + 完整路径），最多 maxResults 个。</summary>
    public static List<(string Name, string Path)> Search(
        IEnumerable<string> roots, string query, CancellationToken ct,
        int maxDepth = DefaultMaxDepth, int maxResults = DefaultMaxResults)
    {
        var results = new List<(string, string)>();
        foreach (var rawRoot in roots)
        {
            if (ct.IsCancellationRequested || results.Count >= maxResults) break;
            var root = Environment.ExpandEnvironmentVariables(rawRoot).Trim();
            if (root.Length == 0 || !Directory.Exists(root)) continue;
            SearchDir(root, query, 0, maxDepth, maxResults, results, ct);
        }
        return results;
    }

    /// <summary>全量建立文件名索引（后台任务调用），最多 IndexEntryCap 条。</summary>
    public static List<(string Name, string Path)> IndexAll(
        IEnumerable<string> roots, CancellationToken ct, int maxDepth = DefaultMaxDepth)
    {
        var entries = new List<(string, string)>();
        foreach (var rawRoot in roots)
        {
            if (ct.IsCancellationRequested || entries.Count >= IndexEntryCap) break;
            var root = Environment.ExpandEnvironmentVariables(rawRoot).Trim();
            if (root.Length == 0 || !Directory.Exists(root)) continue;
            IndexDir(root, 0, maxDepth, entries, ct);
        }
        return entries;
    }

    private static void SearchDir(
        string dir, string query, int depth, int maxDepth, int maxResults,
        List<(string, string)> results, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || depth > maxDepth || results.Count >= maxResults) return;
        var (files, subDirs) = GetListings(dir);

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested || results.Count >= maxResults) return;
            var name = Path.GetFileName(file);
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add((name, file));
            }
        }
        foreach (var sub in subDirs)
        {
            if (ct.IsCancellationRequested || results.Count >= maxResults) return;
            string dirName;
            try { dirName = Path.GetFileName(sub); } catch { continue; }
            if (IsNoiseDir(sub, dirName)) continue;
            SearchDir(sub, query, depth + 1, maxDepth, maxResults, results, ct);
        }
    }

    private static void IndexDir(
        string dir, int depth, int maxDepth, List<(string, string)> entries, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || depth > maxDepth || entries.Count >= IndexEntryCap) return;
        var (files, subDirs) = GetListings(dir);

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested || entries.Count >= IndexEntryCap) return;
            entries.Add((Path.GetFileName(file), file));
        }
        foreach (var sub in subDirs)
        {
            if (ct.IsCancellationRequested || entries.Count >= IndexEntryCap) return;
            string dirName;
            try { dirName = Path.GetFileName(sub); } catch { continue; }
            if (IsNoiseDir(sub, dirName)) continue;
            IndexDir(sub, depth + 1, maxDepth, entries, ct);
        }
    }

    /// <summary>单条文件路径是否应纳入索引（深度 + 父目录噪音检查；用于增量更新）。</summary>
    public static bool ShouldIndexPath(string fullPath, string rootWithSep)
    {
        var depth = CountChar(fullPath, '\\') - CountChar(rootWithSep, '\\');
        if (depth > DefaultMaxDepth) return false;
        var parent = Path.GetDirectoryName(fullPath);
        if (parent != null)
        {
            var name = Path.GetFileName(parent);
            if (name.StartsWith('.') || name.StartsWith('$')) return false;
            if (name is "node_modules" or "bin" or "obj" or "Debug" or "Release"
                or "Temp" or "tmp" or "Cache" or "cache" or "Logs" or "logs") return false;
        }
        return true;
    }

    private static int CountChar(string s, char c)
    {
        var n = 0;
        foreach (var ch in s) if (ch == c) n++;
        return n;
    }

    /// <summary>跳过系统目录、隐藏目录、构建产物目录与链接（避免噪音与循环）。</summary>
    private static bool IsNoiseDir(string fullPath, string name)
    {
        if (name.StartsWith('.') || name.StartsWith('$')) return true;
        if (name is "node_modules" or "bin" or "obj" or "Debug" or "Release"
            or "Temp" or "tmp" or "Cache" or "cache" or "Logs" or "logs") return true;
        try
        {
            var attrs = File.GetAttributes(fullPath);
            if ((attrs & FileAttributes.ReparsePoint) != 0) return true;
            if ((attrs & FileAttributes.Hidden) != 0) return true;
            if ((attrs & FileAttributes.System) != 0) return true;
        }
        catch { return true; }
        return false;
    }
}
