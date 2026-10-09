using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskBuddy.Tools;

/// <summary>Snipaste 式贴图：置顶悬浮，拖动移动、滚轮以鼠标为锚点缩放、右键菜单、双击关闭。
/// 全程物理像素定位（SetWindowPos），150% DPI 下拖动/缩放不抖动。</summary>
public sealed class PinWindow : Window
{
    private readonly Image _image;
    private readonly BitmapSource _src;
    private double _baseW, _baseH;
    private double _scale = 1.0;

    // 物理像素状态（抖动的根源是 DIP 取整，全程用物理整数像素）
    private int _px, _py, _pw, _ph;
    private double _dpi = 1.0;

    private bool _dragging; private Point _dragStart; private int _dragWinPX, _dragWinPY;

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndAfter, int x, int y, int cx, int cy, uint flags);
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    public PinWindow(BitmapSource src, double x, double y, double w, double h)
    {
        _src = src;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;

        var host = new Border
        {
            // 淡绿色渐变描边
            BorderBrush = new LinearGradientBrush(
                new GradientStopCollection {
                    new GradientStop(Color.FromRgb(0x5C, 0xE8, 0xA0), 0),
                    new GradientStop(Color.FromRgb(0x2E, 0xB8, 0x72), 0.5),
                    new GradientStop(Color.FromRgb(0x7D, 0xF0, 0xB0), 1),
                }, new Point(0, 0), new Point(1, 1)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            Background = Brushes.White
        };
        // 高画质：物理像素对齐 + 高质量缩放算法（滚轮缩放时不糊）
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        _image = new Image { Source = src, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(_image, EdgeMode.Aliased);
        host.Child = _image;
        Content = host;

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseWheel += OnWheel;
        MouseDoubleClick += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            // Ctrl+C：贴图原图复制到剪贴板（像素无忧：存的是物理像素原始位图）
            if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                CopyToClipboard();
                e.Handled = true;
            }
        };
        Loaded += (_, _) =>
        {
            Focusable = true; Focus();
            _dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (_dpi <= 0.01) _dpi = 1.0;
            _baseW = w; _baseH = h;
            MovePhys((int)Math.Round(x * _dpi), (int)Math.Round(y * _dpi), (int)Math.Round(w * _dpi), (int)Math.Round(h * _dpi));
        };
        ContextMenu = BuildMenu();
    }

    private ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        var copy = new MenuItem { Header = "复制" }; copy.Click += (_, _) => CopyToClipboard();
        var save = new MenuItem { Header = "保存…" }; save.Click += (_, _) => Save();
        var scale1 = new MenuItem { Header = "缩放 100%" }; scale1.Click += (_, _) => SetScale(1.0, null);
        var close = new MenuItem { Header = "关闭" }; close.Click += (_, _) => Close();
        m.Items.Add(copy); m.Items.Add(save); m.Items.Add(scale1); m.Items.Add(new Separator()); m.Items.Add(close);
        return m;
    }

    /// <summary>把贴图原图（物理像素）复制到剪贴板；成功后给一个小型视觉反馈（描边闪一下绿色）。</summary>
    private void CopyToClipboard()
    {
        try
        {
            Clipboard.SetImage(_src);
            FlashBorder();
        }
        catch { }
    }

    /// <summary>描边闪烁提示已复制。</summary>
    private async void FlashBorder()
    {
        try
        {
            if (Content is Border b)
            {
                var orig = b.BorderBrush;
                b.BorderBrush = new SolidColorBrush(Color.FromRgb(0x5C, 0xE8, 0xA0));
                await Task.Delay(150);
                b.BorderBrush = orig;
            }
        }
        catch { }
    }

    private void Save()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图片 (*.png)|*.png", Title = "保存贴图", FileName = "贴图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") };
        if (dlg.ShowDialog(this) != true) return;
        try { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(_src)); using var fs = File.Create(dlg.FileName); enc.Save(fs); }
        catch (Exception ex) { MessageBox.Show(this, "保存失败：" + ex.Message, "错误"); }
    }

    private IntPtr Hnd => new WindowInteropHelper(this).Handle;

    /// <summary>物理像素直摆（唯一位置出口）。
    /// 抖动根因：WPF 对 Left/Top 赋值会触发自己的 MoveWindow，与 SetWindowPos 竞争 → 跳变。
    /// 解法：完全不用 WPF 定位（赋值会打架），只走 SetWindowPos；WPF 的 Left/Top 从不主动改。
    /// 拖拽期间更不能碰 WPF 布局属性。</summary>
    private void MovePhys(int x, int y, int w, int h)
    {
        _px = x; _py = y; _pw = w; _ph = h;
        SetWindowPos(Hnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void OnDown(object s, MouseButtonEventArgs e)
    {
        _dragging = true;
        // 点击即取焦点（ShowActivated=false 的贴图默认无键盘焦点，Ctrl+C/Ctrl+S 需要）
        try { Focusable = true; Focus(); } catch { }
        _dragStartScreen = GetCursorPosPhys();
        _dragWinPX = _px; _dragWinPY = _py;
        CaptureMouse();
        e.Handled = true;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    private Point _dragStartScreen;

    private static Point GetCursorPosPhys()
    {
        GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    private void OnMove(object s, MouseEventArgs e)
    {
        if (!_dragging) return;
        var cur = GetCursorPosPhys();
        // 物理像素整数位移（基于屏幕坐标，不受窗口自身移动影响 → 零抖动）
        MovePhys(_dragWinPX + (int)Math.Round(cur.X - _dragStartScreen.X),
                _dragWinPY + (int)Math.Round(cur.Y - _dragStartScreen.Y),
                _pw, _ph);
        e.Handled = true;
    }

    private void OnUp(object s, MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnWheel(object s, MouseWheelEventArgs e)
    {
        double f = e.Delta > 0 ? 1.18 : 1 / 1.18;
        // 以鼠标为锚点：鼠标物理屏幕坐标下的点在缩放前后保持原位
        var cur = GetCursorPosPhys();
        double ax = _pw > 0 ? (cur.X - _px) / _pw : 0.5;
        double ay = _ph > 0 ? (cur.Y - _py) / _ph : 0.5;
        SetScale(_scale * f, (ax, ay));
        e.Handled = true;
    }

    private void SetScale(double s, (double ax, double ay)? anchor)
    {
        s = Math.Clamp(s, 0.15, 8.0);
        int newW = (int)Math.Round(_baseW * _dpi * s);
        int newH = (int)Math.Round(_baseH * _dpi * s);
        if (newW < 24 || newH < 24 || newW > 8000 || newH > 8000) return;
        double ax = anchor?.ax ?? 0.5, ay = anchor?.ay ?? 0.5;
        int apx = _px + (int)Math.Round(_pw * ax);
        int apy = _py + (int)Math.Round(_ph * ay);
        _scale = s;
        MovePhys(apx - (int)Math.Round(newW * ax), apy - (int)Math.Round(newH * ay), newW, newH);
    }
}