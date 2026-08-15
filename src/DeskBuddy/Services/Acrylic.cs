using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DeskBuddy.Services;

/// <summary>
/// 通过 SetWindowCompositionAttribute 启用 Win10/11 的亚克力（Acrylic）毛玻璃背景。
/// 关键点：毛玻璃会铺满整个窗口矩形、把圆角填成直角。
/// 因此只在 DWM 圆角（Win11）可用时才启用毛玻璃；否则走半透明回退，保证圆角始终可见。
/// </summary>
public static class Acrylic
{
    private enum AccentState
    {
        Disabled = 0,
        Gradient = 1,
        TransparentGradient = 2,
        BlurBehind = 3,
        AcrylicBlurBehind = 4,
        HostBackdrop = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor; // ABGR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private const int WCA_ACCENT_POLICY = 19;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    /// <summary>
    /// 启用亚克力背景（圆角由 RoundedWindow 的区域裁剪负责）。
    /// </summary>
    public static bool TryEnableRoundedAcrylic(Window window, Theme theme) => Enable(window, theme);

    /// <summary>启用亚克力背景，返回是否成功。</summary>
    public static bool Enable(Window window, Theme theme)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            var accent = new AccentPolicy
            {
                AccentState = (int)AccentState.AcrylicBlurBehind,
                AccentFlags = 0,
                GradientColor = ToAbgr(theme.CardTint, theme.CardAlpha)
            };

            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>()),
                SizeOfData = Marshal.SizeOf<AccentPolicy>()
            };

            try
            {
                Marshal.StructureToPtr(accent, data.Data, false);
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(data.Data);
            }
        }
        catch
        {
            return false;
        }
    }

    private static int ToAbgr(Color c, double alpha)
    {
        int a = (int)Math.Clamp(alpha * 255, 0, 255);
        return (a << 24) | (c.B << 16) | (c.G << 8) | c.R;
    }
}
