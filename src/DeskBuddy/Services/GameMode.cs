using System.Runtime.InteropServices;
using System.Text;

namespace DeskBuddy.Services;

/// <summary>
/// 游戏模式：当前台是「游戏 / 全屏程序」时，屏蔽 DeskBuddy 的全部全局快捷键，
/// 免得在游戏里误触（双击 Alt 弹菜单、按 F1 截图、Esc 被抢）。
/// 按键本身仍然照常传给游戏（钩子只观察、不拦截）。
///
/// 两条判定，任一命中即算（覆盖独占全屏和无边框全屏两类游戏）：
///   1) SHQueryUserNotificationState = D3D 独占全屏(3) / 演示模式(4)；
///   2) 前台窗口的矩形恰好铺满它所在的那块显示器（容差 2px）。
/// 结果缓存 800ms：键盘钩子回调非常频繁，不能每次按键都去查窗口。
/// </summary>
public static class GameMode
{
    // ==================== 状态 ====================

    private static bool _enabled = true;      // 设置面板总开关
    private static bool _cached;
    private static long _lastCheck;
    private static bool _lastActive;

    private const int CheckIntervalMs = 800;

    /// <summary>总开关（设置 → 通用 → 游戏模式）。</summary>
    public static bool Enabled
    {
        get => _enabled;
        set { _enabled = value; _cached = false; }
    }

    /// <summary>当前是否应该屏蔽快捷键（开关打开 + 检测到全屏程序）。</summary>
    public static bool ShouldBlock
    {
        get
        {
            if (!_enabled) return false;
            var now = Environment.TickCount64;
            if (_cached && now - _lastCheck < CheckIntervalMs) return _lastActive;

            _cached = true;
            _lastCheck = now;
            bool active = Detect();
            if (active != _lastActive)
            {
                _lastActive = active;
                DebugLog.Write(active
                    ? "[GAME] 进入游戏模式：所有全局快捷键已屏蔽"
                    : "[GAME] 退出游戏模式：全局快捷键已恢复");
            }
            return _lastActive;
        }
    }

    // ==================== 检测 ====================

    private static bool Detect()
    {
        try
        {
            if (SHQueryUserNotificationState(out var q) == 0 && (q == 3 || q == 4)) return true;
        }
        catch { }

        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero || IsIconic(h)) return false;

            GetWindowThreadProcessId(h, out var pid);
            if (pid == (uint)Environment.ProcessId) return false;   // 自己的窗口（截图覆盖层/贴图/菜单）

            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            var name = cls.ToString();
            if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

            if (!GetWindowRect(h, out var wr)) return false;
            var mon = MonitorFromWindow(h, 2 /*MONITOR_DEFAULTTONEAREST*/);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;

            var m = mi.rcMonitor;
            // 容差 2px：个别游戏会留 1px 边
            return wr.Left <= m.Left + 2 && wr.Top <= m.Top + 2
                && wr.Right >= m.Right - 2 && wr.Bottom >= m.Bottom - 2;
        }
        catch { return false; }
    }

    // ==================== P/Invoke ====================

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
}
