using System.Windows.Media;

namespace QuickMenu.Services;

/// <summary>macOS 风格配色（浅色 / 深色 / 跟随系统）。</summary>
public sealed class Theme
{
    public Color CardTint { get; init; }
    public double CardAlpha { get; init; }
    public Color BorderColor { get; init; }
    public Color TextPrimary { get; init; }
    public Color TextSecondary { get; init; }
    public Color HoverBg { get; init; }
    public Color SelectedBg { get; init; }
    public Color Separator { get; init; }
    public bool IsDark { get; init; }

    public static Theme From(string mode)
    {
        bool dark = mode switch
        {
            "dark" => true,
            "light" => false,
            _ => !IsSystemLightTheme()
        };
        return dark ? Dark : Light;
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch
        {
            return true;
        }
    }

    public static readonly Theme Dark = new()
    {
        CardTint = Color.FromRgb(0x1E, 0x1E, 0x1E),
        CardAlpha = 0.90,
        BorderColor = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF),
        TextPrimary = Color.FromRgb(0xF2, 0xF2, 0xF7),
        TextSecondary = Color.FromRgb(0xA8, 0xA8, 0xB0),
        HoverBg = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF),
        SelectedBg = Color.FromArgb(0x3D, 0x5E, 0xB2, 0xFF),
        Separator = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
        IsDark = true
    };

    public static readonly Theme Light = new()
    {
        CardTint = Color.FromRgb(0xF9, 0xF9, 0xFB),
        CardAlpha = 0.93,
        BorderColor = Color.FromArgb(0x40, 0x00, 0x00, 0x00),
        TextPrimary = Color.FromRgb(0x1D, 0x1D, 0x1F),
        TextSecondary = Color.FromRgb(0x86, 0x86, 0x8C),
        HoverBg = Color.FromArgb(0x16, 0x00, 0x00, 0x00),
        SelectedBg = Color.FromArgb(0x33, 0x0A, 0x84, 0xFF),
        Separator = Color.FromArgb(0x26, 0x00, 0x00, 0x00),
        IsDark = false
    };
}
