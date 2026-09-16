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
/// 定位用 SetWindowPos 物理像素直摆（PerMonitorV2 双屏混合 DPI 下不漂移）。</summary>
public sealed class PinWindow : Window
{
    private readonly Image _image;
    private readonly BitmapSource _src;
    private readonly double _baseW, _baseH;
    private double _scale = 1.0;
    private bool _dragging; private Point _dragStart; private Point _winStart;

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
        Left = x; Top = y;
        Width = Math.Max(24, w); Height = Math.Max(24, h);
        _baseW = Width; _baseH = Height;

        var host = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2)
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
            // 物理坐标直摆：把 WPF DIP 位置换算成物理像素（用窗口当前 DPI）
            var hnd = new WindowInteropHelper(this).Handle;
            double dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            SetWindowPos(hnd, IntPtr.Zero, (int)Math.Round(x * dpi), (int)Math.Round(y * dpi),
                (int)Math.Round(w * dpi), (int)Math.Round(h * dpi), SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
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

    private void OnDown(object s, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _winStart = new Point(Left, Top);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object s, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        Left = _winStart.X + (p.X - _dragStart.X);
        Top = _winStart.Y + (p.Y - _dragStart.Y);
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
        // Snipaste 式：以鼠标位置为锚点缩放（鼠标下的点保持在原位）
        SetScale(_scale * f, e.GetPosition(this));
        e.Handled = true;
    }

    private void SetScale(double s, Point? anchorLocal)
    {
        s = Math.Clamp(s, 0.15, 8.0);
        double newW = _baseW * s, newH = _baseH * s;
        if (newW < 24 || newH < 24 || newW > 8000 || newH > 8000) return;
        double ax = 0.5, ay = 0.5;   // 默认围绕中心
        if (anchorLocal is { } a) { ax = a.X / Math.Max(1, Width); ay = a.Y / Math.Max(1, Height); }
        // 锚点（窗口内比例位置）在缩放前后保持同屏位置
        double px = Left + Width * ax, py = Top + Height * ay;
        _scale = s;
        Width = newW; Height = newH;
        Left = px - newW * ax; Top = py - newH * ay;
    }
}