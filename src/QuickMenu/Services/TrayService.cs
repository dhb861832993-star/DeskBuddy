using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace QuickMenu.Services;

/// <summary>系统托盘图标与右键菜单。</summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _autoStartItem;

    public event Action? ShowMenuRequested;
    public event Action? SettingsRequested;
    public event Action? EditConfigRequested;
    public event Action? ReloadConfigRequested;
    public event Action<bool>? AutoStartChanged;
    public event Action? ExitRequested;

    public TrayService()
    {
        _icon = new NotifyIcon
        {
            Icon = CreateAppIcon(),
            Visible = true,
            Text = "QuickMenu 快启 · 双击 Ctrl 呼出"
        };

        var menu = new ContextMenuStrip();

        var show = new ToolStripMenuItem("打开快速菜单");
        show.Click += (_, _) => ShowMenuRequested?.Invoke();
        menu.Items.Add(show);

        var settings = new ToolStripMenuItem("设置…");
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        menu.Items.Add(settings);

        menu.Items.Add(new ToolStripSeparator());

        var edit = new ToolStripMenuItem("编辑配置文件");
        edit.Click += (_, _) => EditConfigRequested?.Invoke();
        menu.Items.Add(edit);

        var reload = new ToolStripMenuItem("重新加载配置");
        reload.Click += (_, _) => ReloadConfigRequested?.Invoke();
        menu.Items.Add(reload);

        _autoStartItem = new ToolStripMenuItem("开机自动启动") { CheckOnClick = true };
        _autoStartItem.Checked = AutoStart.IsEnabled();
        _autoStartItem.CheckedChanged += (_, _) => AutoStartChanged?.Invoke(_autoStartItem.Checked);
        menu.Items.Add(_autoStartItem);

        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exit);

        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowMenuRequested?.Invoke();
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowMenuRequested?.Invoke();
        };
    }

    /// <summary>更新托盘提示（热键变化时）。</summary>
    public void UpdateHotkeyText(string hotkeyDisplay) =>
        _icon.Text = $"QuickMenu 快启 · 双击 {hotkeyDisplay} 呼出";

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    /// <summary>绘制一个 macOS 风格圆角方块 + “Q” 的托盘图标。</summary>
    private static Icon CreateAppIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRect(new Rectangle(2, 2, 28, 28), 8);
            using var brush = new LinearGradientBrush(
                new Point(2, 2), new Point(30, 30),
                Color.FromArgb(0x2A, 0x6D, 0xF4), Color.FromArgb(0x7A, 0x9E, 0xFA));
            g.FillPath(brush, path);

            using var font = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString("Q", font);
            g.DrawString("Q", font, Brushes.White, (32 - size.Width) / 2, (32 - size.Height) / 2 - 1);
        }

        var h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
