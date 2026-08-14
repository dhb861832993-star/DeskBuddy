using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace QuickMenu.Services;

/// <summary>
/// 用 SetWindowRgn 把窗口裁剪成圆角。
/// 解决“亚克力毛玻璃铺满矩形窗口、四角看起来是直角”的问题：
/// 区域裁剪对窗口整体（包括毛玻璃效果）生效，圆角确定性地可见。
/// </summary>
public static class RoundedWindow
{
    public static void Apply(Window window, double cornerRadiusDip)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            // 窗口高度会动态变化（菜单条目数），尺寸一变就重建区域
            window.SizeChanged += (_, _) => ApplyRegion(window, hwnd, cornerRadiusDip);
            ApplyRegion(window, hwnd, cornerRadiusDip);
        };
    }

    private static void ApplyRegion(Window window, IntPtr hwnd, double cornerRadiusDip)
    {
        if (hwnd == IntPtr.Zero) return;
        if (GetClientRect(hwnd, out var rect) == 0) return;
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return;

        double scale = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        int radius = Math.Max(1, (int)Math.Round(cornerRadiusDip * scale));
        int d = radius * 2;

        var rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, d, d);
        if (rgn != IntPtr.Zero)
        {
            // SetWindowRgn 成功后窗口接管 region 所有权（由系统负责释放）
            SetWindowRgn(hwnd, rgn, true);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
}
