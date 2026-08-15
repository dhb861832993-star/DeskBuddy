using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using QuickMenu.Models;

namespace QuickMenu.Services;

/// <summary>把拖拽进来的文件 / 快捷方式 / 网址转换成菜单条目。</summary>
public static class ItemDropHandler
{
    /// <summary>从拖拽数据（文件 / 文本 URL）生成菜单条目。</summary>
    public static List<QuickMenuItem> FromDrop(System.Windows.IDataObject data)
    {
        if (data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            if (data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
            {
                return FromFiles(files);
            }
        }

        if (data.GetDataPresent(System.Windows.DataFormats.UnicodeText) &&
            data.GetData(System.Windows.DataFormats.UnicodeText) is string text)
        {
            text = text.Trim();
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return new List<QuickMenuItem>
                {
                    new() { Name = uri.Host, Type = "url", Path = uri.ToString() }
                };
            }
        }
        return new List<QuickMenuItem>();
    }

    /// <summary>判断拖拽数据是否可接受（文件或网页地址文本）。</summary>
    public static bool IsAcceptable(System.Windows.IDataObject data)
    {
        if (data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return true;
        if (data.GetDataPresent(System.Windows.DataFormats.UnicodeText) &&
            data.GetData(System.Windows.DataFormats.UnicodeText) is string s)
        {
            s = s.Trim();
            return Uri.TryCreate(s, UriKind.Absolute, out var u) &&
                   (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        }
        return false;
    }

    /// <summary>根据文件路径列表生成菜单条目（快捷方式 / 网址 / exe / 文件夹 / 普通文件）。</summary>
    public static List<QuickMenuItem> FromFiles(IEnumerable<string> paths)
    {
        var result = new List<QuickMenuItem>();
        foreach (var p in paths)
        {
            var item = FromFile(p);
            if (item != null) result.Add(item);
        }
        return result;
    }

    private static QuickMenuItem? FromFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var ext = Path.GetExtension(path).ToLowerInvariant();

            if (ext == ".lnk")
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var (target, args) = ResolveLnk(path);
                if (!string.IsNullOrEmpty(target))
                {
                    if (Directory.Exists(target))
                        return new QuickMenuItem { Name = name, Type = "folder", Path = target };
                    if (File.Exists(target))
                    {
                        // 带参数的快捷方式（典型如 cmd /c 启动脚本）：保留 .lnk 本体，
                        // 由系统按快捷方式原始语义启动（目标+参数+工作目录+图标全部正确），
                        // 避免只取目标路径导致参数丢失（例如只打开空 cmd 窗口）
                        if (!string.IsNullOrWhiteSpace(args))
                            return new QuickMenuItem { Name = name, Type = "file", Path = path };
                        return new QuickMenuItem { Name = name, Type = "app", Path = target };
                    }
                }
                return new QuickMenuItem { Name = name, Type = "file", Path = path };
            }

            if (ext == ".url")
            {
                var name = Path.GetFileNameWithoutExtension(path);
                string? url = null;
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                    {
                        url = line[4..].Trim();
                        break;
                    }
                }
                if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out _))
                {
                    return new QuickMenuItem { Name = name, Type = "url", Path = url };
                }
                return new QuickMenuItem { Name = name, Type = "file", Path = path };
            }

            if (Directory.Exists(path))
            {
                return new QuickMenuItem
                {
                    Name = Path.GetFileName(path.TrimEnd('\\', '/')),
                    Type = "folder",
                    Path = path
                };
            }

            if (File.Exists(path))
            {
                var name = Path.GetFileName(path);
                if (ext is ".exe" or ".bat" or ".cmd")
                {
                    return new QuickMenuItem { Name = Path.GetFileNameWithoutExtension(path), Type = "app", Path = path };
                }
                return new QuickMenuItem { Name = name, Type = "file", Path = path };
            }
        }
        catch
        {
            // 忽略无法读取的文件
        }
        return null;
    }

    /// <summary>解析 .lnk 快捷方式的目标路径和参数（IShellLink COM）。</summary>
    private static (string Target, string Args) ResolveLnk(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var args = new StringBuilder(1024);
            link.GetArguments(args, args.Capacity);
            return (sb.Length > 0 ? sb.ToString() : "", args.ToString());
        }
        catch
        {
            return ("", "");
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFileName);
    }
}
