using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using DeskBuddy.Services;

namespace DeskBuddy.Tools;

/// <summary>
/// 顶边画廊：鼠标顶到显示器最上沿（或把贴图往上拖）时，从顶部滑出一排截图画廊。
/// 苹果风：圆角卡片 + 半透明深色底 + 柔和投影 + 缓动滑入；点卡片还原成可继续编辑的贴图。
/// </summary>
public sealed class GalleryWindow : Window
{
    public static GalleryWindow? Current { get; private set; }

    // 布局常量（DIP）
    private const double Pad = 16, CardW = 160, CardH = 100, Gap = 14, LabelH = 18;
    private const double SlideOff = PanelH + 28;
    public const double PanelH = 166;

    private readonly Border _panel;
    private readonly StackPanel _row;
    private readonly TranslateTransform _slide = new(0, -SlideOff);
    private bool _shown;

    /// <summary>点了某张缩略图（path）——App 启动时挂上「还原成贴图」。</summary>
    public static Action<string>? OnPicked;

    /// <summary>懒创建单例（顶边触发和贴图拖放都走这里，避免谁先谁后的问题）。</summary>
    public static GalleryWindow Ensure()
    {
        if (Current == null)
        {
            Current = new GalleryWindow();
            DebugLog.Write("[GALLERY] 已创建画廊窗口");
        }
        return Current;
    }

    public bool IsShown => _shown;

    public GalleryWindow()
    {
        Current = this;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 560;
        Height = PanelH;
        Visibility = Visibility.Collapsed;

        _panel = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x11, 0x15, 0x1D)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(Pad),
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.5, Color = Colors.Black },
            RenderTransform = _slide,
        };
        _row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _panel.Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _row,
        };
        Content = _panel;
    }

    // ==================== 内容 ====================

    /// <summary>重新读盘并重建缩略图行。</summary>
    public void Refresh()
    {
        _row.Children.Clear();
        var items = Gallery.Load();

        if (items.Count == 0)
        {
            Width = 380;
            _row.Children.Add(new TextBlock
            {
                Text = "截图后把贴图往屏幕顶部拖，就会收进画廊",
                Foreground = new SolidColorBrush(Color.FromRgb(0x7E, 0x8D, 0xA3)),
                FontSize = 12.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(4, 34, 4, 0),
            });
            return;
        }

        foreach (var it in items) _row.Children.Add(BuildCard(it));
        int n = Math.Min(items.Count, 5);
        Width = Math.Min(940, Pad * 2 + n * CardW + (n - 1) * Gap);
    }

    private UIElement BuildCard(GalleryItem it)
    {
        var img = new Border
        {
            Width = CardW, Height = CardH, CornerRadius = new CornerRadius(10),
            Background = new ImageBrush(it.Thumb) { Stretch = Stretch.UniformToFill },
            Cursor = Cursors.Hand,
            ToolTip = $"{it.Path}\n点击 → 还原成贴图（可继续编辑）",
        };

        // 悬浮删除
        var del = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x00, 0x00, 0x00)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand,
            ToolTip = "从画廊删除",
            Child = new TextBlock
            {
                Text = "✕", FontSize = 11, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        del.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Gallery.Delete(it.Path);
            Refresh();
        };

        var thumbHost = new Grid { Width = CardW, Height = CardH };
        thumbHost.Children.Add(img);
        thumbHost.Children.Add(del);

        var time = new TextBlock
        {
            Text = it.Label, FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x9A, 0xB0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var card = new StackPanel
        {
            Width = CardW,
            Margin = new Thickness(0, 0, Gap, 0),
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var scale = new ScaleTransform(1, 1);
        card.RenderTransform = scale;
        card.Children.Add(thumbHost);
        card.Children.Add(time);

        card.MouseEnter += (_, _) =>
        {
            Animate(scale, ScaleTransform.ScaleXProperty, 1.05);
            Animate(scale, ScaleTransform.ScaleYProperty, 1.05);
            del.Visibility = Visibility.Visible;
        };
        card.MouseLeave += (_, _) =>
        {
            Animate(scale, ScaleTransform.ScaleXProperty, 1.0);
            Animate(scale, ScaleTransform.ScaleYProperty, 1.0);
            del.Visibility = Visibility.Collapsed;
        };
        img.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            HideNow();
            OnPicked?.Invoke(it.Path);
        };
        return card;
    }

    private static void Animate(IAnimatable target, DependencyProperty prop, double to)
    {
        target.BeginAnimation(prop, new DoubleAnimation(to, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // ==================== 显示 / 隐藏 ====================

    /// <summary>在鼠标所在显示器顶部居中弹出（物理像素坐标）。</summary>
    public void ShowOn(int physX, int physY)
    {
        if (_shown) return;          // 已经露出就不重复读盘/重排
        Refresh();
        var (mon, scale) = MonitorOf(physX, physY);
        int pw = (int)Math.Round(Width * scale);
        int ph = (int)Math.Round(PanelH * scale);
        int px = mon.Left + (mon.Width - pw) / 2;
        int py = mon.Top + (int)Math.Round(10 * scale);
        PlaceWindow(px, py, pw, ph);

        if (_shown) return;
        _shown = true;
        Visibility = Visibility.Visible;
        _slide.BeginAnimation(TranslateTransform.YProperty, null);
        _slide.Y = -SlideOff;
        _slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-SlideOff, 0, TimeSpan.FromMilliseconds(230))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    /// <summary>带滑动动画收起。</summary>
    public void HideAnimated()
    {
        if (!_shown) return;
        _shown = false;
        var anim = new DoubleAnimation(0, -SlideOff, TimeSpan.FromMilliseconds(150))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        anim.Completed += (_, _) => { if (!_shown) Visibility = Visibility.Collapsed; };
        _slide.BeginAnimation(TranslateTransform.YProperty, anim);
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120)));
    }

    /// <summary>立刻收起（点完卡片/开始截图时用）。</summary>
    public void HideNow()
    {
        _shown = false;
        _slide.BeginAnimation(TranslateTransform.YProperty, null);
        BeginAnimation(OpacityProperty, null);
        Visibility = Visibility.Collapsed;
    }

    private void PlaceWindow(int x, int y, int w, int h)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            SetWindowPos(hwnd, new IntPtr(-1) /*HWND_TOPMOST*/, x, y, w, h, SWP_NOACTIVATE);
        }
        catch { }
    }

    /// <summary>物理像素点是否落在画廊面板内（贴图拖放判定用）。</summary>
    public bool ContainsPhysical(int x, int y)
    {
        if (!_shown || !IsVisible) return false;
        try
        {
            var tl = _panel.PointToScreen(new Point(0, 0));
            var br = _panel.PointToScreen(new Point(Math.Max(1, _panel.ActualWidth), Math.Max(1, _panel.ActualHeight)));
            return x >= tl.X - 8 && x <= br.X + 8 && y >= tl.Y - 8 && y <= br.Y + 8;
        }
        catch { return false; }
    }

    // ==================== 屏幕判定 ====================

    /// <summary>光标是否顶在某个显示器的上沿（触发画廊）。</summary>
    public static bool InTopBand(int physX, int physY)
    {
        var (m, _) = MonitorOf(physX, physY);
        return physY <= m.Top + 3 && physX >= m.Left && physX < m.Right;
    }

    /// <summary>贴图拖拽时：靠近显示器顶部 150 DIP（≈ 面板高度）以内就露出画廊，方便松手落进面板。</summary>
    public static bool NearTopForDrag(int physX, int physY)
    {
        var (m, scale) = MonitorOf(physX, physY);
        return physY <= m.Top + (int)(150 * scale) && physX >= m.Left && physX < m.Right;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFO mi);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr h, int type, out uint dx, out uint dy);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>点所在的显示器矩形（物理像素）与该显示器缩放。</summary>
    private static (RECT Mon, double Scale) MonitorOf(int x, int y)
    {
        var pt = new POINT { X = x, Y = y };
        var hmon = MonitorFromPoint(pt, 2 /*MONITOR_DEFAULTTONEAREST*/);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hmon, ref mi)) return (new RECT { Right = 1920, Bottom = 1080 }, 1.0);
        double scale = 1.0;
        try
        {
            if (GetDpiForMonitor(hmon, 0, out var dpi, out _) == 0 && dpi > 0) scale = dpi / 96.0;
        }
        catch { }
        return (mi.rcMonitor, scale);
    }
}
