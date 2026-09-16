using System;
using System.IO;
using System.Runtime.InteropServices;
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
        _image = new Image { Source = src, Stretch = Stretch.Uniform };
        host.Child = _image;
        Content = host;

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseWheel += OnWheel;
        MouseDoubleClick += (_, _) => Close();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
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
        var copy = new MenuItem { Header = "复制" }; copy.Click += (_, _) => { try { Clipboard.SetImage(_src); } catch { } };
        var save = new MenuItem { Header = "保存…" }; save.Click += (_, _) => Save();
        var scale1 = new MenuItem { Header = "缩放 100%" }; scale1.Click += (_, _) => SetScale(1.0, null);
        var close = new MenuItem { Header = "关闭" }; close.Click += (_, _) => Close();
        m.Items.Add(copy); m.Items.Add(save); m.Items.Add(scale1); m.Items.Add(new Separator()); m.Items.Add(close);
        return m;
    }

    private void Save()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图片 (*.png)|*.png", Title = "保存贴图", FileName = "贴图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") };
        if (dlg.ShowDialog(this) != true) return;
        try { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(_src)); using var fs = File.Create(dlg.FileName); enc.Save(fs); }
        catch (Exception ex) { MessageBox.Show(this, "保存失败：" + ex.Message, "错误"); }
    }

    private IntPtr Hnd => new WindowInteropHelper(this).Handle;

    /// <summary>物理像素直摆 + 同步 WPF DIP（消除 Win32 与 WPF 两套位置打架导致的抖动）。</summary>
    private void MovePhys(int x, int y, int w, int h)
    {
        _px = x; _py = y; _pw = w; _ph = h;
        SetWindowPos(Hnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        // 关键：同步 WPF 期望值，否则 WPF 会用自己的 Left/Top 再摆一次（互相打架→抖动）
        Left = x / _dpi; Top = y / _dpi; Width = w / _dpi; Height = h / _dpi;
    }

    private void OnDown(object s, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragWinPX = _px; _dragWinPY = _py;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object s, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        int dx = (int)Math.Round((p.X - _dragStart.X) * _dpi);
        int dy = (int)Math.Round((p.Y - _dragStart.Y) * _dpi);
        MovePhys(_dragWinPX + dx, _dragWinPY + dy, _pw, _ph);
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
        // 以鼠标为锚点：鼠标下的物理点在缩放前后保持原位
        var lp = e.GetPosition(this);
        double ax = _pw > 0 ? lp.X * _dpi / _pw : 0.5;
        double ay = _ph > 0 ? lp.Y * _dpi / _ph : 0.5;
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