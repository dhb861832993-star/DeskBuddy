using System.IO;
using System.Windows.Media.Imaging;

namespace DeskBuddy.Services;

/// <summary>画廊里的一张截图。</summary>
public sealed class GalleryItem
{
    public required string Path { get; init; }
    public required BitmapSource Thumb { get; init; }
    public DateTime Taken { get; init; }
    public string Label => Taken.ToString("HH:mm");
}

/// <summary>
/// 截图画廊的本地存储：一张图一个 PNG 文件，文件名带时间戳，按修改时间倒序取最近的。
/// 目录在程序同级 gallery\（和 config/memo 一致，便携）。
/// </summary>
public static class Gallery
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "gallery");

    private static void EnsureDir() { try { Directory.CreateDirectory(Dir); } catch { } }

    /// <summary>最近 N 张（缩略图解码，只解到 320px 宽，省内存）。</summary>
    public static List<GalleryItem> Load(int max = 12)
    {
        var list = new List<GalleryItem>();
        try
        {
            EnsureDir();
            var files = new DirectoryInfo(Dir).GetFiles("*.png")
                .OrderByDescending(f => f.LastWriteTime)
                .Take(max);
            foreach (var f in files)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(f.FullName);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 360;
                    bmp.EndInit();
                    bmp.Freeze();
                    list.Add(new GalleryItem { Path = f.FullName, Thumb = bmp, Taken = f.LastWriteTime });
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>存一张（原始分辨率），返回落盘路径。</summary>
    public static string? Save(BitmapSource src)
    {
        try
        {
            EnsureDir();
            var name = $"shot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
            var path = Path.Combine(Dir, name);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var fs = File.Create(path);
            enc.Save(fs);
            return path;
        }
        catch { return null; }
    }

    /// <summary>取原图（还原成贴图用）。</summary>
    public static BitmapSource? LoadFull(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public static void Delete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
