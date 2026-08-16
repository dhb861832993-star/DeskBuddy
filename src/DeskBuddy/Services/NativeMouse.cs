using System.Runtime.InteropServices;
using System.Windows;

namespace DeskBuddy.Services;

/// <summary>
/// 鼠标屏幕坐标辅助：拖拽窗口时用 GetCursorPos（物理像素）作为唯一参考系，
/// 避免 WPF 事件参数里 GetPosition 的参考系随窗口移动而变化，导致拖拽回跳/抖动。
/// </summary>
public static class NativeMouse
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>获取鼠标当前位置（物理屏幕像素）。</summary>
    public static Point GetScreenPosition()
    {
        GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }
}
