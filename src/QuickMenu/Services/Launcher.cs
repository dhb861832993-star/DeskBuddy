using System.Diagnostics;
using System.IO;
using QuickMenu.Models;

namespace QuickMenu.Services;

/// <summary>负责解析并启动各种类型的条目。</summary>
public static class Launcher
{
    private static readonly Dictionary<string, string?> ResolveCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>把“程序名”解析为完整 exe 路径（PATH + 常见安装目录）。</summary>
    public static string? Resolve(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath)) return null;
        var key = nameOrPath.Trim();
        if (ResolveCache.TryGetValue(key, out var cached)) return cached;
        var result = ResolveCore(key);
        ResolveCache[key] = result;
        return result;
    }

    private static string? ResolveCore(string raw)
    {
        var expanded = Environment.ExpandEnvironmentVariables(raw);
        if (File.Exists(expanded)) return expanded;
        if (Path.IsPathRooted(expanded)) return null; // 绝对路径但文件不存在

        var exe = expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? expanded
            : expanded + ".exe";

        // 1) PATH
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir.Trim(), exe);
            if (File.Exists(p)) return p;
        }

        // 2) 常见安装目录
        var dirs = new List<string>();
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(pf)) dirs.Add(pf);
        if (!string.IsNullOrEmpty(pfx)) dirs.Add(pfx);
        dirs.Add(Path.Combine(la, "Programs"));
        dirs.Add(Path.Combine(la, "Microsoft", "WindowsApps"));

        foreach (var dir in dirs)
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
            var p2 = Path.Combine(dir, "Microsoft", exe);
            if (File.Exists(p2)) return p2;
        }
        return null;
    }

    /// <summary>启动条目。返回是否成功。</summary>
    public static bool Launch(QuickMenuItem item)
    {
        try
        {
            var psi = new ProcessStartInfo { UseShellExecute = true };
            switch (item.Type)
            {
                case "url":
                    if (!Uri.TryCreate(item.Path, UriKind.Absolute, out var uri)) return false;
                    psi.FileName = uri.ToString();
                    break;

                case "folder":
                    psi.FileName = "explorer.exe";
                    psi.Arguments = $"\"{Environment.ExpandEnvironmentVariables(item.Path)}\"";
                    break;

                case "command":
                    psi.FileName = Environment.ExpandEnvironmentVariables(item.Path);
                    psi.Arguments = item.Args ?? "";
                    break;

                case "file":
                    psi.FileName = Environment.ExpandEnvironmentVariables(item.Path);
                    break;

                default: // app
                    var exe = Resolve(item.Path);
                    if (exe == null)
                    {
                        System.Windows.MessageBox.Show($"找不到程序：{item.Path}\n请在配置文件中填写完整路径。", "QuickMenu");
                        return false;
                    }
                    psi.FileName = exe;
                    if (!string.IsNullOrWhiteSpace(item.Args)) psi.Arguments = item.Args;
                    break;
            }
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"启动失败：{item.Name}\n{ex.Message}", "QuickMenu");
            return false;
        }
    }
}
