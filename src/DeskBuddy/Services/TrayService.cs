using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DeskBuddy.Services;

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
            Text = "DeskBuddy 快启 · 双击 Ctrl 呼出"
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
        _icon.Text = $"DeskBuddy 快启 · 双击 {hotkeyDisplay} 呼出";

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    /// <summary>绘制品牌托盘图标：渐变圆角方块 + 白色机器人伙伴脸。</summary>
    private static Icon CreateAppIcon()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size;

            using var path = RoundedRect(new Rectangle(0, 0, size, size), (int)(s * 0.22));
            using var brush = new LinearGradientBrush(
                new Point(0, 0), new Point(size, size),
                Color.FromArgb(0x7A, 0x5C, 0xFF), Color.FromArgb(0x2A, 0x6D, 0xF4));
            var blend = new ColorBlend(3)
            {
                Colors = new[] { Color.FromArgb(0x7A, 0x5C, 0xFF), Color.FromArgb(0x2A, 0x6D, 0xF4), Color.FromArgb(0x32, 0xD5, 0x83) },
                Positions = new[] { 0f, 0.55f, 1f }
            };
            brush.InterpolationColors = blend;
            g.FillPath(brush, path);

            using var penWhite = new Pen(Color.White, Math.Max(1, s * 0.028f));
            penWhite.StartCap = LineCap.Round; penWhite.EndCap = LineCap.Round;
            g.DrawLine(penWhite, s * 0.5f, s * 0.30f, s * 0.5f, s * 0.205f);
            using (var ball = new SolidBrush(Color.White))
                g.FillEllipse(ball, s * 0.5f - s * 0.035f, s * 0.17f - s * 0.035f, s * 0.07f, s * 0.07f);

            using var headPath = RoundedRect(new Rectangle((int)(s * 0.30f), (int)(s * 0.30f), (int)(s * 0.40f), (int)(s * 0.42f)), (int)(s * 0.11f));
            using (var head = new SolidBrush(Color.White))
                g.FillPath(head, headPath);

            var dark = Color.FromArgb(0x2A, 0x2A, 0x3A);
            using var eye = new SolidBrush(dark);
            float eyeR = s * 0.045f;
            g.FillEllipse(eye, s * 0.415f - eyeR, s * 0.47f - eyeR, eyeR * 2, eyeR * 2);
            g.FillEllipse(eye, s * 0.585f - eyeR, s * 0.47f - eyeR, eyeR * 2, eyeR * 2);

            using var smile = new Pen(dark, Math.Max(1, s * 0.03f));
            smile.StartCap = LineCap.Round; smile.EndCap = LineCap.Round;
            g.DrawArc(smile, s * 0.42f, s * 0.53f, s * 0.16f, s * 0.12f, 20, 140);
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
