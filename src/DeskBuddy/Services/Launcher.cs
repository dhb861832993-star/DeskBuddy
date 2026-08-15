using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using DeskBuddy.Models;

namespace DeskBuddy.Services;

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
    public static bool Launch(DeskBuddyItem item)
    {
        try
        {
            var psi = new ProcessStartInfo { UseShellExecute = true };
            switch (item.Type)
            {
                case "url":
                    if (!Uri.TryCreate(item.Path, UriKind.Absolute, out var uri)) return false;
                    // 本机服务地址：先探测是否已在运行。
                    // 已运行 → 直接打开页面（浏览器弹出/聚焦，不重复启动服务）；
                    // 未运行 → 若配置了启动脚本（Args=脚本路径），先拉起服务（脚本就绪后自己会开浏览器）。
                    if (IsLocalHost(uri))
                    {
                        if (IsReachable(uri))
                        {
                            psi.FileName = uri.ToString();
                            break;
                        }
                        var fallback = Environment.ExpandEnvironmentVariables(item.Args ?? "");
                        if (File.Exists(fallback))
                        {
                            Process.Start(new ProcessStartInfo { FileName = fallback, UseShellExecute = true });
                            return true;
                        }
                    }
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
                        System.Windows.MessageBox.Show($"找不到程序：{item.Path}\n请在配置文件中填写完整路径。", "DeskBuddy");
                        return false;
                    }
                    // 程序已在运行 → 只把它弹出到前台，不重复启动
                    if (BringExistingToFront(exe)) return true;
                    psi.FileName = exe;
                    if (!string.IsNullOrWhiteSpace(item.Args)) psi.Arguments = item.Args;
                    break;
            }
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"启动失败：{item.Name}\n{ex.Message}", "DeskBuddy");
            return false;
        }
    }

    /// <summary>用系统默认程序打开一个文件/文件夹路径（文件搜索结果的启动方式）。</summary>
    public static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ExpandEnvironmentVariables(path),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"打开失败：{path}\n{ex.Message}", "DeskBuddy");
        }
    }

    /// <summary>目标程序是否本机地址（127.0.0.1 / localhost / ::1）。</summary>
    private static bool IsLocalHost(Uri uri) =>
        uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>探测本机端口是否在监听（连接成功即视为服务已运行）。</summary>
    private static bool IsReachable(Uri uri)
    {
        try
        {
            var port = uri.Port > 0 ? uri.Port : (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80);
            using var tcp = new TcpClient();
            var ar = tcp.BeginConnect(uri.Host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(1500)) return false;
            tcp.EndConnect(ar);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 同名进程已在运行 → 找到它的窗口并弹出到前台（最小化先还原、隐藏先显示），
    /// 返回 true（不再启动新实例）。找不到任何窗口的进程（纯后台进程）返回 false 交给正常启动。
    /// </summary>
    private static bool BringExistingToFront(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        try
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p.Id == Environment.ProcessId) continue;
                var hwnd = FindMainWindow(p.Id);
                if (hwnd == IntPtr.Zero) continue;
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);          // 最小化 → 还原
                else if (!IsWindowVisible(hwnd)) ShowWindow(hwnd, SW_SHOW); // 关到托盘等隐藏态 → 显示
                ForceForeground(hwnd);
                return true;
            }
        }
        catch
        {
            // 进程消失 / 权限不足等：走正常启动
        }
        return false;
    }

    /// <summary>找到进程的“主”顶层窗口：优先可见且非 IME 辅助窗；没有可见的则取第一个带标题的窗口。</summary>
    private static IntPtr FindMainWindow(int pid)
    {
        IntPtr best = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var wpid);
            if (wpid != pid) return true;
            var title = GetWindowText(hwnd);
            if (title == "Default IME" || title == "MSCTFIME UI" || title.Length == 0) return true;
            if (best == IntPtr.Zero) best = hwnd;
            if (IsWindowVisible(hwnd))
            {
                best = hwnd;
                return false; // 优先可见窗口
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>更可靠地把窗口置前（解除 Windows 的前台锁定限制）。</summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        try
        {
            var fg = GetForegroundWindow();
            var fgThread = GetWindowThreadProcessId(fg, out _);
            var curThread = GetCurrentThreadId();
            var attached = false;
            if (fgThread != curThread && fgThread != 0)
            {
                AttachThreadInput(curThread, fgThread, true);
                attached = true;
            }
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            if (attached) AttachThreadInput(curThread, fgThread, false);
        }
        catch { }
    }

    private static string GetWindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
}
