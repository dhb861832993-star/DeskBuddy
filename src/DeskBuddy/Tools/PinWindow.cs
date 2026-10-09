using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DeskBuddy.Tools;

/// <summary>Snipaste 式贴图：置顶悬浮，拖动移动、滚轮以鼠标为锚点缩放、画笔标注、右键菜单、双击关闭。
/// 全程物理像素定位（SetWindowPos），150% DPI 下拖动/缩放不抖动。
/// 标注模式：工具条进入画笔后，拖动=画线，滚轮=调粗细（1-24px），色板选色（默认红）；
/// Ctrl+P/Ctrl+E 切换；复制/保存输出「图+标注」合成图。</summary>
public sealed class PinWindow : Window
{
    private readonly Image _image;
    private readonly Canvas _ink;         // 标注层（笔画）
    private readonly Canvas _overlay;     // 覆盖层（工具条 placeholder——笔画画在._ink)
    private readonly Grid _layers;
    private readonly Border _host;
    private readonly BitmapSource _src;
    private double _baseW, _baseH;
    private double _scale = 1.0;

    // ===== 画笔状态 =====
    private bool _inking;                 // 标注模式
    private Color _penColor = Color.FromRgb(0xFF, 0x3B, 0x30);   // 默认红
    private double _penW = 3.0;           // DIP 粗细
    private const double PenWMin = 1, PenWMax = 24;
    private bool _strokeActive;           // 正在画
    private Point _strokeLast;
    private Polyline _currentStroke;
    private readonly List<Polyline> _strokes = new();
    private Border? _toolPanel;
    private Border? _thickBubble;         // 粗细预览气泡

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

        _host = new Border
        {
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
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        _image = new Image { Source = src, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _ink = new Canvas { IsHitTestVisible = true };
        _overlay = new Canvas { IsHitTestVisible = false };
        _layers = new Grid();
        _layers.Children.Add(_image);
        _layers.Children.Add(_ink);
        _host.Child = _layers;
        Content = _host;

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseWheel += OnWheel;
        MouseDoubleClick += (_, _) => Close();
        PreviewKeyDown += OnKey;
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

    private void OnKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 标注模式下 Esc 先退出标注；否则关窗
            if (_inking) { SetInking(false); }
            else Close();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            CopyToClipboard(); e.Handled = true; return;
        }
        if (e.Key == Key.P && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SetInking(!_inking); e.Handled = true; return;
        }
        if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            UndoStroke(); e.Handled = true; return;
        }
        // 数字键快速选色（标注模式）：1红 2黄 3绿 4蓝 5白 6黑
        if (_inking && e.Key >= Key.D1 && e.Key <= Key.D6)
        {
            _penColor = (e.Key - Key.D1) switch
            {
                0 => Color.FromRgb(0xFF, 0x3B, 0x30),
                1 => Color.FromRgb(0xFF, 0xCC, 0x00),
                2 => Color.FromRgb(0x30, 0xD1, 0x58),
                3 => Color.FromRgb(0x0A, 0x84, 0xFF),
                4 => Colors.White,
                _ => Colors.Black,
            };
            RefreshToolPanel();
            e.Handled = true;
        }
    }

    // ==================== 模式切换 ====================

    /// <summary>画笔模式开/关：Cursor 变化 + 工具条显示。</summary>
    private void SetInking(bool on)
    {
        _inking = on;
        Cursor = on ? Cursors.Pen : Cursors.Arrow;
        if (on) EnsureToolPanel();
        else if (_toolPanel?.Parent is Panel p) p.Children.Remove(_toolPanel);
        if (_toolPanel != null && _ink.Parent != null)
        {
            // 工具条挂到 _ink 同层（_overlay 未用，挂 _layers 顶层）
            if (on)
            {
                if (_toolPanel.Parent is Panel p2) p2.Children.Remove(_toolPanel);
                _layers.Children.Add(_toolPanel);
                PositionToolPanel();
            }
        }
    }

    /// <summary>画笔工具条：色板（6色 + 当前高亮）+ 粗细显示 + 撤销 + 完成。</summary>
    private void EnsureToolPanel()
    {
        if (_toolPanel != null) { RefreshToolPanel(); return; }
        _toolPanel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x5C, 0xE8, 0xA0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 5, 8, 5),
            IsHitTestVisible = true,
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        // 色板 6 色按钮
        var colors = new[] {
            (Color.FromRgb(0xFF,0x3B,0x30), "红色"),
            (Color.FromRgb(0xFF,0xCC,0x00), "黄色"),
            (Color.FromRgb(0x30,0xD1,0x58), "绿色"),
            (Color.FromRgb(0x0A,0x84,0xFF), "蓝色"),
            (Colors.White, "白色"),
            (Colors.Black, "黑色"),
        };
        foreach (var (c, _) in colors)
        {
            var dot = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(c),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = c,
            };
            ToolTipService.SetToolTip(dot, "选择颜色");
            dot.MouseLeftButtonDown += (s, e) => { _penColor = c; RefreshToolPanel(); e.Handled = true; };
            sp.Children.Add(dot);
        }
        _toolPanel.Child = sp;
        RefreshToolPanel();
    }

    /// <summary>工具条刷新（当前色高亮 + 粗细文本）。</summary>
    private void RefreshToolPanel()
    {
        if (_toolPanel?.Child is not StackPanel sp) return;
        // 色点高亮
        foreach (var el in sp.Children)
        {
            if (el is Border b && b.Tag is Color c)
            {
                b.BorderBrush = c == _penColor
                    ? new SolidColorBrush(Color.FromRgb(0x5C, 0xE8, 0xA0))
                    : new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0));
                b.BorderThickness = c == _penColor ? new Thickness(2) : new Thickness(1);
                b.Width = c == _penColor ? 18 : 16;
                b.Height = c == _penColor ? 18 : 16;
            }
        }
    }

    /// <summary>工具条贴窗口右下角（画布 DIP 坐标）。</summary>
    private void PositionToolPanel()
    {
        if (_toolPanel == null) return;
        _toolPanel.Measure(new Size(_layers.ActualWidth, _layers.ActualHeight));
        double tw = _toolPanel.DesiredSize.Width, th = _toolPanel.DesiredSize.Height;
        double x = Math.Max(4, _layers.ActualWidth - tw - 8);
        double y = 4;   // 顶部（标注线通常在中间，顶部不挡）
        _toolPanel.SetValue(Canvas.LeftProperty, x);
        _toolPanel.SetValue(Canvas.TopProperty, y);
    }

    /// <summary>撤销最后一笔。</summary>
    private void UndoStroke()
    {
        if (_strokes.Count == 0) return;
        var last = _strokes[^1];
        _ink.Children.Remove(last);
        _strokes.RemoveAt(_strokes.Count - 1);
    }

    // ==================== 鼠标（拖动/画笔两态） ====================

    private void OnDown(object s, MouseButtonEventArgs e)
    {
        try { Focusable = true; Focus(); } catch { }
        // 工具条内部：放行（色点点击自己处理）
        if (IsFromToolPanel(e)) { e.Handled = false; return; }

        if (_inking)
        {
            // 开始一笔
            var p = e.GetPosition(_ink);
            _strokeActive = true;
            _strokeLast = p;
            _currentStroke = new Polyline
            {
                Stroke = new SolidColorBrush(_penColor),
                StrokeThickness = _penW,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            _currentStroke.Points.Add(p);
            _ink.Children.Add(_currentStroke);
        }
        else
        {
            _dragging = true;
            _dragStartScreen = GetCursorPosPhys();
            _dragWinPX = _px; _dragWinPY = _py;
        }
        CaptureMouse();
        e.Handled = true;
    }

    private bool IsFromToolPanel(MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d != null)
        {
            if (ReferenceEquals(d, _toolPanel)) return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void OnMove(object s, MouseEventArgs e)
    {
        if (_inking && _strokeActive)
        {
            var p = e.GetPosition(_ink);
            // 距离阈值：过滤微动（减噪点）
            if ((p - _strokeLast).Length > 1.2)
            {
                _currentStroke?.Points.Add(p);
                _strokeLast = p;
            }
            e.Handled = true;
            return;
        }
        if (!_dragging) return;
        var cur = GetCursorPosPhys();
        MovePhys(_dragWinPX + (int)Math.Round(cur.X - _dragStartScreen.X),
                _dragWinPY + (int)Math.Round(cur.Y - _dragStartScreen.Y),
                _pw, _ph);
        e.Handled = true;
    }

    private void OnUp(object s, MouseButtonEventArgs e)
    {
        if (_inking && _strokeActive)
        {
            _strokeActive = false;
            if (_currentStroke != null)
            {
                if (_currentStroke.Points.Count > 1) _strokes.Add(_currentStroke);
                else _ink.Children.Remove(_currentStroke);   // 单点不算笔画
                _currentStroke = null;
            }
        }
        else
        {
            _dragging = false;
        }
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnWheel(object s, MouseWheelEventArgs e)
    {
        if (_inking)
        {
            // 标注模式：滚轮调画笔粗细（1-24）
            var nw = _penW + (e.Delta > 0 ? 0.5 : -0.5);
            _penW = Math.Clamp(nw, PenWMin, PenWMax);
            ShowThicknessBubble(e.GetPosition(_ink));
            e.Handled = true;
            return;
        }
        double f = e.Delta > 0 ? 1.18 : 1 / 1.18;
        var cur = GetCursorPosPhys();
        double ax = _pw > 0 ? (cur.X - _px) / _pw : 0.5;
        double ay = _ph > 0 ? (cur.Y - _py) / _ph : 0.5;
        SetScale(_scale * f, (ax, ay));
        e.Handled = true;
    }

    /// <summary>粗细预览气泡（滚轮调粗细时短暂显示当前 px 值）。</summary>
    private async void ShowThicknessBubble(Point at)
    {
        try
        {
            if (_thickBubble == null)
            {
                _thickBubble = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1E)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 3, 8, 3),
                    IsHitTestVisible = false,
                    Child = new TextBlock { Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold },
                };
            }
            if (_thickBubble.Parent is Panel p) p.Children.Remove(_thickBubble);
            if (_thickBubble.Child is TextBlock tb) tb.Text = $"画笔 {_penW:0.#} px";
            _layers.Children.Add(_thickBubble);
            _thickBubble.Measure(new Size(_layers.ActualWidth, _layers.ActualHeight));
            _thickBubble.SetValue(Canvas.LeftProperty, Math.Max(4, Math.Min(at.X + 14, _layers.ActualWidth - _thickBubble.DesiredSize.Width - 4)));
            _thickBubble.SetValue(Canvas.TopProperty, Math.Max(4, at.Y - _thickBubble.DesiredSize.Height - 8));
            await Task.Delay(900);
            if (_thickBubble?.Parent is Panel p2) p2.Children.Remove(_thickBubble);
        }
        catch { }
    }

    // ==================== 菜单 / 输出 ====================

    private ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        var draw = new MenuItem { Header = "画笔标注（Ctrl+P）" }; draw.Click += (_, _) => SetInking(true);
        var undo = new MenuItem { Header = "撤销一笔（Ctrl+Z）" }; undo.Click += (_, _) => UndoStroke();
        var copy = new MenuItem { Header = "复制（含标注）" }; copy.Click += (_, _) => CopyToClipboard();
        var save = new MenuItem { Header = "保存…" }; save.Click += (_, _) => Save();
        var scale1 = new MenuItem { Header = "缩放 100%" }; scale1.Click += (_, _) => SetScale(1.0, null);
        var close = new MenuItem { Header = "关闭" }; close.Click += (_, _) => Close();
        m.Items.Add(draw); m.Items.Add(undo); m.Items.Add(new Separator());
        m.Items.Add(copy); m.Items.Add(save); m.Items.Add(scale1); m.Items.Add(new Separator()); m.Items.Add(close);
        return m;
    }

    /// <summary>合成「底图 + 标注」为统一位图（DIP 尺寸 × DPI = 物理分辨率）。</summary>
    private RenderTargetBitmap? Compose()
    {
        try
        {
            // 临时隐藏工具条/气泡再截
            var tp = _toolPanel; SetInkingPanelVisible(false);
            var bp = _thickBubble; if (bp?.Parent is Panel p2) p2.Children.Remove(bp);
            var w = _layers.ActualWidth > 1 ? _layers.ActualWidth : _baseW;
            var h = _layers.ActualHeight > 1 ? _layers.ActualHeight : _baseH;
            var dpi = _dpi > 0.01 ? _dpi : 1.0;
            var rtb = new RenderTargetBitmap((int)Math.Round(w * dpi), (int)Math.Round(h * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            rtb.Render(_layers);
            rtb.Freeze();
            SetInkingPanelVisible(true, tp);
            return rtb;
        }
        catch { return null; }
    }

    private void SetInkingPanelVisible(bool on, Border? panel = null)
    {
        var tp = panel ?? _toolPanel;
        if (tp != null && _inking)
        {
            if (on)
            {
                if (tp.Parent is not Panel) { _layers.Children.Add(tp); PositionToolPanel(); }
            }
            else if (tp.Parent is Panel p) p.Children.Remove(tp);
        }
    }

    private void CopyToClipboard()
    {
        try
        {
            // 无标注：直接原图（零损耗）；有标注：合成
            if (_strokes.Count == 0)
            {
                Clipboard.SetImage(_src);
            }
            else
            {
                var composed = Compose();
                if (composed != null) Clipboard.SetImage(composed);
            }
            FlashBorder();
        }
        catch { }
    }

    private void Save()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图片 (*.png)|*.png", Title = "保存贴图", FileName = "贴图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            BitmapSource bs = _strokes.Count > 0 ? (Compose() ?? _src) : _src;
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bs));
            using var fs = File.Create(dlg.FileName); enc.Save(fs);
        }
        catch (Exception ex) { MessageBox.Show(this, "保存失败：" + ex.Message, "错误"); }
    }

    private async void FlashBorder()
    {
        try
        {
            var orig = _host.BorderBrush;
            _host.BorderBrush = new SolidColorBrush(Color.FromRgb(0x5C, 0xE8, 0xA0));
            await Task.Delay(150);
            _host.BorderBrush = orig;
        }
        catch { }
    }

    // ==================== 物理 / 缩放 ====================

    private IntPtr Hnd => new WindowInteropHelper(this).Handle;

    private void MovePhys(int x, int y, int w, int h)
    {
        _px = x; _py = y; _pw = w; _ph = h;
        SetWindowPos(Hnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        if (_toolPanel != null && _inking) PositionToolPanel();
        if (_overlay != null) { _overlay.Width = _layers.ActualWidth; _overlay.Height = _layers.ActualHeight; }
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    private Point _dragStartScreen;

    private static Point GetCursorPosPhys()
    {
        GetCursorPos(out var p);
        return new Point(p.X, p.Y);
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