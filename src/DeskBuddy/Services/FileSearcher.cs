using System.IO;

namespace DeskBuddy.Services;

/// <summary>
/// 按配置的搜索范围（目录路径）在后台查找文件名包含关键字的文件。
/// 限制遍历深度与结果数量，跳过系统/构建噪音目录，支持取消。
/// </summary>
public static class FileSearcher
{
    private const int DefaultMaxDepth = 5;
    private const int DefaultMaxResults = 12;

    /// <summary>返回匹配的文件（名称 + 完整路径），最多 maxResults 个。</summary>
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

    private static void SearchDir(
        string dir, string query, int depth, int maxDepth, int maxResults,
        List<(string, string)> results, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || depth > maxDepth || results.Count >= maxResults) return;

        string[] files;
        string[] subDirs;
        try
        {
            files = Directory.GetFiles(dir);
            subDirs = Directory.GetDirectories(dir);
        }
        catch
        {
            return; // 无权限 / 被占用等，跳过该目录
        }

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

    /// <summary>跳过系统目录、隐藏目录、构建产物目录（避免噪音与链接循环）。</summary>
    private static bool IsNoiseDir(string fullPath, string name)
    {
        if (name.StartsWith('.') || name.StartsWith('$')) return true;
        if (name is "node_modules" or "bin" or "obj" or "Debug" or "Release"
            or "Temp" or "tmp" or "Cache" or "cache" or "Logs" or "logs") return true;
        try
        {
            var attrs = File.GetAttributes(fullPath);
            if ((attrs & FileAttributes.ReparsePoint) != 0) return true; // 链接/挂载点，防循环
            if ((attrs & FileAttributes.Hidden) != 0) return true;
            if ((attrs & FileAttributes.System) != 0) return true;
        }
        catch { return true; }
        return false;
    }
}
