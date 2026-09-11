using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskBuddy.Tools;

/// <summary>Snipaste 式贴图：置顶悬浮显示截图，可拖动、滚轮缩放、复制、保存、关闭。</summary>
public sealed class PinWindow : Window
{
    private readonly Image _image;
    private readonly BitmapSource _src;
    private double _scale = 1.0;
    private bool _dragging; private Point _dragStart; private Point _winStart;

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
        Loaded += (_, _) => { Focusable = true; Focus(); };
        ContextMenu = BuildMenu();
    }

    private ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        var copy = new MenuItem { Header = "复制" }; copy.Click += (_, _) => { try { Clipboard.SetImage(_src); } catch { } };
        var save = new MenuItem { Header = "保存…" }; save.Click += (_, _) => Save();
        var scale1 = new MenuItem { Header = "缩放 100%" }; scale1.Click += (_, _) => SetScale(1.0);
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
        double f = e.Delta > 0 ? 1.15 : 1 / 1.15;
        SetScale(_scale * f);
        e.Handled = true;
    }

    private void SetScale(double s)
    {
        _scale = Math.Clamp(s, 0.15, 8.0);
        // 以图片显示尺寸缩放（保持拉伸比例）
        double baseW = _src.PixelWidth, baseH = _src.PixelHeight;
        var ratio = Math.Min(Width / baseW, Height / baseH);
        double newW = baseW * _scale * (ratio > 0 ? ratio / _scale * _scale : 1);
        // 简化：直接按当前尺寸乘缩放增量，围绕中心
        newW = Width * _scale;
        double newH = Height * _scale;
        if (newW < 30 || newH < 30 || newW > 4000 || newH > 4000) return;
        double cx = Left + Width / 2, cy = Top + Height / 2;
        Width = newW; Height = newH;
        Left = cx - newW / 2; Top = cy - newH / 2;
    }
}