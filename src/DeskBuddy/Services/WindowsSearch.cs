using System.Data.OleDb;

namespace DeskBuddy.Services;

/// <summary>
/// Windows Search（系统索引）文件搜索后端：通过 OleDb 直查 SystemIndex 目录。
/// 与 Listary 同思路 —— 不自己把文件名塞进内存，而是查询系统服务维护的增量索引
/// （NTFS USN 日志增量更新），可支撑千万级文件，没有内置索引的 30 万条上限。
/// </summary>
public static class WindowsSearch
{
    internal const string ConnStr = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

    /// <summary>Windows Search 是否可用（服务在跑 + 提供程序可连接）。</summary>
    public static bool IsAvailable()
    {
        try
        {
            using var conn = new OleDbConnection(ConnStr);
            conn.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>在指定范围内按文件名子串搜索（忽略大小写），最多 limit 条。失败或未索引时返回空列表。</summary>
    public static List<(string Name, string Path)> Search(string query, string[] scopes, int limit = 12)
    {
        var results = new List<(string, string)>();
        try
        {
            var q = query.Trim();
            if (q.Length == 0) return results;
            // LIKE 通配符转义，避免用户输入的 % _ 被当作通配符
            q = q.Replace("%", "[%]").Replace("_", "[_]").Replace("'", "''");

            var scopeSql = new List<string>();
            foreach (var raw in scopes)
            {
                var root = Environment.ExpandEnvironmentVariables(raw).Trim();
                if (root.Length == 0) continue;
                scopeSql.Add($"SCOPE='file:{root.Replace("'", "''")}'");
            }
            if (scopeSql.Count == 0) return results;

            var where = string.Join(" OR ", scopeSql);
            var sql = $"SELECT TOP {limit} System.ItemUrl FROM SystemIndex WHERE ({where}) AND System.FileName LIKE '%{q}%'";

            using var conn = new OleDbConnection(ConnStr);
            conn.Open();
            using var cmd = new OleDbCommand(sql, conn);
            cmd.CommandTimeout = 10;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var url = reader.GetValue(0)?.ToString();
                var path = UrlToPath(url);
                if (string.IsNullOrEmpty(path)) continue;
                results.Add((System.IO.Path.GetFileName(path), path));
            }
        }
        catch
        {
            // 查询失败（如范围尚未被系统索引）→ 返回空，由调用方回退内置索引
        }
        return results;
    }

    /// <summary>把 System.ItemUrl（如 file:C:/a/b.txt）转成 Windows 路径。</summary>
    private static string UrlToPath(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (!url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return "";
        var p = url.Substring(5);
        if (p.StartsWith("//")) p = p.Substring(2);
        try { p = Uri.UnescapeDataString(p); } catch { }
        return p.Replace('/', '\\');
    }
}
