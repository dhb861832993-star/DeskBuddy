using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskBuddy.Services;

/// <summary>从 exe / ico / png 提取图标（带缓存，线程安全）。</summary>
public static class IconProvider
{
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetFileIcon(string path, string? customIcon = null)
    {
        var src = string.IsNullOrWhiteSpace(customIcon) ? path : customIcon;
        if (string.IsNullOrWhiteSpace(src)) return null;

        var key = src;
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        ImageSource? icon = null;
        if (File.Exists(src))
        {
            var info = new SHFILEINFO();
            var r = SHGetFileInfo(src, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_LARGEICON);
            if (r != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                try
                {
                    using var bmp = Icon.FromHandle(info.hIcon).ToBitmap();
                    using var ms = new MemoryStream();
                    bmp.Save(ms, ImageFormat.Png);
                    ms.Position = 0;
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = ms;
                    bi.EndInit();
                    icon = bi;
                }
                finally
                {
                    DestroyIcon(info.hIcon);
                }
            }
        }

        if (icon != null) icon.Freeze();
        lock (CacheLock)
        {
            if (!Cache.ContainsKey(key)) Cache[key] = icon;
        }
        return icon;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
