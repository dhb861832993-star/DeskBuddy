using System.Data.OleDb;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeskBuddy.Services;

/// <summary>
/// Windows Search 索引范围管理：自动把搜索根目录加入系统索引（像 Listary 等商业软件一样），
/// 并提供索引进度查询（供右下角进度条使用）。
/// 策略：优先 COM API（ISearchCrawlScopeManager.AddUserScopeRule，正常机器可用）；
/// 失败时降级为引导用户（打开系统「索引选项」）。
/// </summary>
public static partial class IndexScope
{
    private const string CrawlKey = @"SOFTWARE\Microsoft\Windows Search\CrawlScopeManager\Windows\SystemIndex";
    private const int LargeScopeThreshold = 3000;   // 超过该文件数视为「大目录」，显示进度条
    private const int TotalCountCap = 2000000;      // 统计总量时的上限（防止大目录枚举过久）
    private const int MaxDepth = 5;

    // ==================== COM 互操作（Search API） ====================

    [ComImport, Guid("7D096C5F-AC08-4F1F-BEB7-5C22C517CE39")]
    private class CSearchManager { }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF69")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchManager
    {
        [PreserveSig]
        int GetCatalog([MarshalAs(UnmanagedType.LPWStr)] string pszCatalog,
                       [Out, MarshalAs(UnmanagedType.Interface)] out ISearchCatalogManager ppSearchCatalogManager);
    }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF50")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchCatalogManager
    {
        [PreserveSig] int GetCatalogStatus(out int pStatus);
        [PreserveSig] int GetCatalogState(out int pReason);
        [PreserveSig] int GetNumberOfItems(out int plCount);
        [PreserveSig] int GetConnectVersion(out int pdwVersion);
        [PreserveSig] int GetPersistentItems(IntPtr ppSearchPersistentItems);
        [PreserveSig] int GetQueryHelper([MarshalAs(UnmanagedType.Interface)] out object ppSearchQueryHelper);
        [PreserveSig] int GetCrawlScopeManager([MarshalAs(UnmanagedType.Interface)] out ISearchCrawlScopeManager ppSearchCrawlScopeManager);
        [PreserveSig] int GetURLToIndex([MarshalAs(UnmanagedType.LPWStr)] string pszURL, [MarshalAs(UnmanagedType.Interface)] out object ppItem);
        [PreserveSig] int GetItemsToIndex([MarshalAs(UnmanagedType.LPWStr)] string pszURL, [MarshalAs(UnmanagedType.Interface)] out object ppItem);
        [PreserveSig] int PutURLToIndex([MarshalAs(UnmanagedType.LPWStr)] string pszURL, [MarshalAs(UnmanagedType.Interface)] object pItem);
        [PreserveSig] int Reset();
        [PreserveSig] int Reindex();
        [PreserveSig] int ReindexSearchRoot([MarshalAs(UnmanagedType.LPWStr)] string pszRootURL);
        [PreserveSig] int ReindexMatchingURLs([MarshalAs(UnmanagedType.LPWStr)] string pszPattern);
        [PreserveSig] int SetParameter(IntPtr propkey, IntPtr varValue);
        [PreserveSig] int GetParameter(IntPtr propkey, IntPtr pvarValue);
    }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF55")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchCrawlScopeManager
    {
        [PreserveSig] int AddDefaultScopeRule([MarshalAs(UnmanagedType.LPWStr)] string pszRule, int fIncludeOverride, int eRuleLevel);
        [PreserveSig] int AddUserScopeRule([MarshalAs(UnmanagedType.LPWStr)] string pszRule, int fIncludeOverride, int eRuleLevel);
        [PreserveSig] int RemoveDefaultScopeRule([MarshalAs(UnmanagedType.LPWStr)] string pszRule);
        [PreserveSig] int RemoveUserScopeRule([MarshalAs(UnmanagedType.LPWStr)] string pszRule);
        [PreserveSig] int AddScopeRule(IntPtr pSearchScopeRule);
        [PreserveSig] int RemoveScopeRule(IntPtr pSearchScopeRule);
        [PreserveSig] int EnumerateRoots(IntPtr ppSearchRootEnum);
        [PreserveSig] int EnumerateScopeRules(IntPtr ppSearchScopeRuleEnum);
        [PreserveSig] int HasSearchRoot([MarshalAs(UnmanagedType.LPWStr)] string pszRoot, out int pfHasSearchRoot);
        [PreserveSig] int GetParentScopeVersionId([MarshalAs(UnmanagedType.LPWStr)] string pszURL, out int plScopeVersionId);
        [PreserveSig] int GetScopeVersionId([MarshalAs(UnmanagedType.LPWStr)] string pszURL, out int plScopeVersionId);
        [PreserveSig] int GetScopeVersionMajor([MarshalAs(UnmanagedType.LPWStr)] string pszURL, out int plScopeVersionMajor);
    }

    // ==================== 自动添加范围 ====================

    /// <summary>确保搜索根目录都在系统索引范围内；返回本次新增的目录列表（新增且未在索引配置中）。</summary>
    public static List<string> EnsureScopes(IEnumerable<string> roots)
    {
        var added = new List<string>();
        foreach (var raw in roots)
        {
            var root = Environment.ExpandEnvironmentVariables(raw).Trim().TrimEnd('\\');
            if (root.Length == 0 || !Directory.Exists(root)) continue;
            if (IsConfigured(root)) continue;
            if (AddViaCom(root))
            {
                added.Add(root);
            }
            else
            {
                DebugLog.Write($"IndexScope.EnsureScopes: COM 添加失败，{root} 需要手动配置索引范围");
            }
        }
        return added;
    }

    /// <summary>该目录是否已被系统索引覆盖（读注册表 CrawlScopeManager 规则判断）。</summary>
    public static bool IsConfigured(string root)
    {
        var target = root.TrimEnd('\\') + "\\";
        try
        {
            // 1) WorkingSetRules：URL 前缀匹配即视为已覆盖
            using (var key = Registry.LocalMachine.OpenSubKey(CrawlKey + @"\WorkingSetRules"))
            {
                if (key != null)
                {
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        using var rk = key.OpenSubKey(sub);
                        var url = rk?.GetValue("URL") as string;
                        if (string.IsNullOrEmpty(url)) continue;
                        var include = rk?.GetValue("Include") is int i && i == 1;
                        if (!include) continue; // 只看包含规则（排除规则视为未配置）
                        var path = ExtractPath(url);
                        if (path.StartsWith(target, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            // 2) SearchRoots：整盘根覆盖（如 file:///E:\[guid]\）
            using (var key = Registry.LocalMachine.OpenSubKey(CrawlKey + @"\SearchRoots"))
            {
                if (key != null)
                {
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        using var rk = key.OpenSubKey(sub);
                        var url = rk?.GetValue("URL") as string;
                        if (string.IsNullOrEmpty(url) || !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;
                        var path = ExtractPath(url);
                        if (target.StartsWith(path, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
        }
        catch { /* 读不到注册表按未配置处理 */ }
        return false;
    }

    private static bool AddViaCom(string root)
    {
        try
        {
            var mgr = (ISearchManager)new CSearchManager();
            ISearchCatalogManager? catalog;
            int hr = mgr.GetCatalog("SystemIndex", out catalog);
            if (hr != 0 || catalog is null) return false;
            ISearchCrawlScopeManager? csm;
            int hrC = catalog.GetCrawlScopeManager(out csm);
            if (hrC != 0 || csm is null) return false;
            // 规则 URL：file:///E:\Future\
            var rule = "file:///" + root.Replace('\\', '/') + "/";
            int hrA = csm.AddUserScopeRule(rule, 1, 1); // fIncludeOverride=TRUE, SEARCH_SCOPE_RULE_LEVEL_INCLUDE
            return hrA == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把索引规则 URL（file:///E:\[卷GUID]\Future\）还原为普通路径（E:\Future\）。</summary>
    private static string ExtractPath(string url)
    {
        var p = url.Replace("file:///", "", StringComparison.OrdinalIgnoreCase);
        var m = VolumeGuidRegex().Match(p);
        if (m.Success) return m.Groups[1].Value + m.Groups[2].Value;
        return p.TrimStart('/');
    }

    [GeneratedRegex(@"^([A-Za-z]:\\)\[[0-9a-fA-F-]+\]\\(.*)$")]
    private static partial Regex VolumeGuidRegex();

    // ==================== 索引进度 ====================

    /// <summary>目录文件总量缓存（枚举大目录耗时，缓存 30 分钟避免每次重扫）。</summary>
    private static readonly Dictionary<string, (long Total, DateTime At)> TotalCache = new();
    private static readonly object TotalLock = new();

    /// <summary>目录文件总量（后台枚举，深度限制 + 上限）。</summary>
    public static long CountFiles(string[] roots)
    {
        long total = 0;
        foreach (var raw in roots)
        {
            var root = Environment.ExpandEnvironmentVariables(raw).Trim();
            if (root.Length == 0 || !Directory.Exists(root)) continue;
            lock (TotalLock)
            {
                if (TotalCache.TryGetValue(root, out var hit) && (DateTime.UtcNow - hit.At).TotalMinutes < 30)
                {
                    total += hit.Total;
                    continue;
                }
            }
            var n = CountDir(root, 0);
            lock (TotalLock) { TotalCache[root] = (n, DateTime.UtcNow); }
            total += n;
            if (total >= TotalCountCap) return TotalCountCap;
        }
        return total;
    }

    private static long CountDir(string dir, int depth)
    {
        if (depth > MaxDepth) return 0;
        long n = 0;
        try { n += Directory.GetFiles(dir).LongLength; } catch { }
        if (depth >= MaxDepth) return n;
        string[] subs;
        try { subs = Directory.GetDirectories(dir); } catch { return n; }
        foreach (var s in subs)
        {
            if (n >= TotalCountCap) return n;
            var name = Path.GetFileName(s);
            if (name.StartsWith('.') || name.StartsWith('$')) continue;
            if (name is "node_modules" or "bin" or "obj" or "Debug" or "Release" or "Temp" or "tmp" or "Cache" or "cache" or "Logs" or "logs") continue;
            try
            {
                var attrs = File.GetAttributes(s);
                if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                if ((attrs & FileAttributes.Hidden) != 0) continue;
                if ((attrs & FileAttributes.System) != 0) continue;
            }
            catch { continue; }
            n += CountDir(s, depth + 1);
            if (n >= TotalCountCap) return n;
        }
        return n;
    }

    /// <summary>指定范围内已被系统索引的文件数（OleDb 查 SystemIndex）。
    /// 注意：聚合 COUNT(*) 在索引爬取过程中可能报 E_FAIL，改用 TOP + 逐行计数（上限 200 万）。</summary>
    public static long CountIndexed(string[] roots)
    {
        long total = 0;
        try
        {
            using var conn = new OleDbConnection(WindowsSearch.ConnStr);
            conn.Open();
            foreach (var raw in roots)
            {
                var root = Environment.ExpandEnvironmentVariables(raw).Trim();
                if (root.Length == 0) continue;
                using var cmd = new OleDbCommand(
                    $"SELECT TOP 2000000 System.ItemUrl FROM SystemIndex WHERE SCOPE='file:{root.Replace("'", "''")}'", conn);
                cmd.CommandTimeout = 30;
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) total++;
            }
        }
        catch { /* 查询失败返回已统计部分 */ }
        return total;
    }

    /// <summary>是否为「大目录」（文件数超过阈值，需要显示进度条）。</summary>
    public static bool IsLargeScope(string[] roots)
    {
        // 先看已索引数量：已全部索引就不算大目录（避免重复提示）
        if (CountIndexed(roots) > 0) return false;
        var total = CountFiles(roots);
        DebugLog.Write($"IndexScope.IsLargeScope: total={total} threshold={LargeScopeThreshold}");
        return total >= LargeScopeThreshold;
    }

    /// <summary>是否正在爬取中的大目录（已索引一部分、总量仍大于已索引数，如用户手动加入后系统正在索引）。
    /// 用「快速判大」只数到阈值即停，秒级返回，不阻塞。</summary>
    public static bool IsCrawling(string[] roots)
    {
        long indexed;
        try { indexed = CountIndexed(roots); } catch { return false; }
        if (indexed <= 0) return false;
        var large = IsLargeEnough(roots, LargeScopeThreshold);
        DebugLog.Write($"IndexScope.IsCrawling: indexed={indexed} large={large}");
        return large;
    }

    /// <summary>快速判断目录是否达到阈值文件数（数到阈值即停，不统计全量）。</summary>
    public static bool IsLargeEnough(string[] roots, long threshold)
    {
        long n = 0;
        foreach (var raw in roots)
        {
            var root = Environment.ExpandEnvironmentVariables(raw).Trim();
            if (root.Length == 0 || !Directory.Exists(root)) continue;
            n += CountDirFast(root, 0, threshold - n);
            if (n >= threshold) return true;
        }
        return n >= threshold;
    }

    private static long CountDirFast(string dir, int depth, long limit)
    {
        if (depth > MaxDepth || limit <= 0) return 0;
        long n = 0;
        try { n += Directory.GetFiles(dir).LongLength; } catch { }
        if (n >= limit) return n;
        if (depth >= MaxDepth) return n;
        string[] subs;
        try { subs = Directory.GetDirectories(dir); } catch { return n; }
        foreach (var s in subs)
        {
            if (n >= limit) return n;
            var name = Path.GetFileName(s);
            if (name.StartsWith('.') || name.StartsWith('$')) continue;
            if (name is "node_modules" or "bin" or "obj" or "Debug" or "Release" or "Temp" or "tmp" or "Cache" or "cache" or "Logs" or "logs") continue;
            try
            {
                var attrs = File.GetAttributes(s);
                if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                if ((attrs & FileAttributes.Hidden) != 0) continue;
                if ((attrs & FileAttributes.System) != 0) continue;
            }
            catch { continue; }
            n += CountDirFast(s, depth + 1, limit - n);
            if (n >= limit) return n;
        }
        return n;
    }
}
