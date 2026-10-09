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
using DeskBuddy.Services;

namespace DeskBuddy.Tools;

/// <summary>Snipaste 式贴图：置顶悬浮。右下角常驻标注工具条（画笔/矩形/颜色/撤销），
/// 拖动=移动（选工具后=绘制），滚轮=调画笔粗细（或无工具时缩放），
/// 拖动画笔画线、矩形框工具画矩形；复制/保存输出合成图。</summary>
public sealed class PinWindow : Window
{
    private readonly Image _image;
    private readonly Canvas _ink;         // 标注层（笔画/矩形）
    private readonly Canvas _uiCanvas = new();   // 装饰层（工具条/气泡，Canvas 点定位防 Grid 遮挡）
    private readonly Grid _layers;
    private readonly Border _host;
    private readonly BitmapSource _src;
    private double _baseW, _baseH;
    private double _scale = 1.0;

    // ===== 标注状态（常驻工具条，无"模式"概念）=====
    private int _tool = 0;                // 0=无(拖动/缩放) 1=画笔 2=矩形
    private Color _penColor = Color.FromRgb(0xFF, 0x3B, 0x30);   // 默认红
    private double _penW = 3.0;
    private const double PenWMin = 1, PenWMax = 24;

    // 绘制中
    private bool _strokeActive;
    private Point _strokeAnchor;
    private Polyline? _currentStroke;
    private Rectangle? _currentRect;
    private readonly List<UIElement> _strokes = new();

    private Border? _toolPanel;
    private Border? _thickBubble;

    // 物理像素状态
    private int _px, _py, _pw, _ph;
    private double _dpi = 1.0;
    private bool _dragging; private Point _dragStart; private int _dragWinPX, _dragWinPY;

    private const string ToolNone = "✥", ToolPen = "✏", ToolRect = "▭";

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndAfter, int x, int y, int cx, int cy, uint flags);
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    public PinWindow(BitmapSource src, double x, double y, double w, double h)
    {
        _src = src;
        WindowStyle = WindowStyle.None;
        // 不用 AllowsTransparency：分层窗口在高 DPI 的 GDI 位图渲染有已知怪病（图不显示）；
        // 圆角Border改为直角——可靠性优先
        AllowsTransparency = false;
        Background = Brushes.White;
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
        // Image 显式布局尺寸 + Uniform：窗口尺寸（DIP）= 选区 DIP（图为物理像素高清）
        // ★布局教训（二分实测 Grid 变体 V_C ✓ / V_B ✗）：
        //   1) Image 必须放 Grid 才能正确 Stretch 铺开（直接当 Border/Window 的 Content 布局协商失败→不渲染）
        //   2) Grid 的非定位子元素（如工具条 Border）会 Stretch 填满整格 → 深色工具条背景盖住整张图！
        //   → 装饰（工具条/气泡）必须放【Canvas 定位层】（Canvas 只按 Left/Top 画，绝不铺满）
        _image = new Image
        {
            Source = src,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _ink = new Canvas { IsHitTestVisible = true };
        _layers = new Grid();
        _layers.Children.Add(_image);        // 第1层：底图（Grid 布局铺开 ✓）
        _layers.Children.Add(_ink);           // 第2层：笔迹
        _layers.Children.Add(_uiCanvas);      // 第3层：装饰（Canvas 点定位，工具条/气泡绝不遮挡）
        _host.Child = _layers;
        _layers.SizeChanged += (_, _) => PositionToolPanel();
        Content = _host;

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseWheel += OnWheel;
        MouseDoubleClick += (_, _) => Close();
        PreviewKeyDown += OnKey;
        // 关键：窗口显式 DIP 尺寸（WPF Auto 内容尺寸 = 0 → 图渲染 0×0 只剩工具条——同截图黑屏bug）
        Width = Math.Max(24, w);
        Height = Math.Max(24, h);
        Left = x; Top = y;

        Loaded += (_, _) =>
        {
            Focusable = true; Focus();
            _dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (_dpi <= 0.01) _dpi = 1.0;
            _baseW = w; _baseH = h;
            _px = (int)Math.Round(x * _dpi); _py = (int)Math.Round(y * _dpi);
            _pw = (int)Math.Round(w * _dpi); _ph = (int)Math.Round(h * _dpi);
            DebugLog.Write($"[PIN] loaded: W={Width:F0}xH{Height:F0} dpi={_dpi} src={src.PixelWidth}x{src.PixelHeight} pr={_pw}x{_ph}");
            // 初始位置由 WPF Left/Top/Width/Height(DIP) 自理——不 SetWindowPos（避免物理/DIP 打架裁切）
            BuildToolPanel();   // 常驻工具条
        };
        ContextMenu = BuildMenu();
    }

    private void OnKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { CopyToClipboard(); e.Handled = true; return; }
        if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { UndoStroke(); e.Handled = true; return; }
        // 1/2/3 快捷选工具
        if (e.Key == Key.D1) { SetTool(0); e.Handled = true; return; }
        if (e.Key == Key.D2) { SetTool(1); e.Handled = true; return; }
        if (e.Key == Key.D3) { SetTool(2); e.Handled = true; return; }
        if (e.Key >= Key.D4 && e.Key <= Key.D9)
        {
            _penColor = (e.Key - Key.D4) switch
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

    // ==================== 常驻工具条（右下角） ====================

    /// <summary>构建标注工具条：工具三选（拖动/画笔/矩形）+ 色板 + 撤销。</summary>
    private void BuildToolPanel()
    {
        _toolPanel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0xFF, 0xB0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(7, 5, 7, 5),
            IsHitTestVisible = true,
            Cursor = Cursors.Arrow,
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };

        // 工具按钮：拖动 / 画笔 / 矩形
        for (int t = 0; t <= 2; t++)
        {
            var glyph = t == 0 ? ToolNone : t == 1 ? ToolPen : ToolRect;
            var tip = t == 0 ? "移动/缩放（1）" : t == 1 ? "画笔（2）" : "矩形（3）";
            var b = MkTool(glyph, tip, () => SetTool(t));
            b.Tag = t;
            sp.Children.Add(b);
        }
        sp.Children.Add(Sep());
        // 色板
        var colors = new[] {
            Color.FromRgb(0xFF,0x3B,0x30), Color.FromRgb(0xFF,0xCC,0x00), Color.FromRgb(0x30,0xD1,0x58),
            Color.FromRgb(0x0A,0x84,0xFF), Colors.White, Colors.Black,
        };
        foreach (var c in colors)
        {
            var dot = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(c),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand, Margin = new Thickness(3, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = c,
            };
            dot.MouseLeftButtonDown += (s, e) => { _penColor = c; RefreshToolPanel(); e.Handled = true; };
            sp.Children.Add(dot);
        }
        sp.Children.Add(Sep());
        // 撤销
        sp.Children.Add(MkTool("↩", "撤销一笔（Ctrl+Z）", UndoStroke));

        _toolPanel.Child = sp;
        _uiCanvas.Children.Add(_toolPanel);
        RefreshToolPanel();
        PositionToolPanel();
    }

    private static System.Windows.Shapes.Path Sep() => new()
    {
        Data = Geometry.Parse("M 0 0 L 0 18"),
        Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
        StrokeThickness = 1, Margin = new Thickness(5, 0, 5, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Button MkTool(string glyph, string tip, Action act)
    {
        var b = new Button
        {
            Content = glyph, ToolTip = tip, FontSize = 13,
            Foreground = Brushes.White, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(7, 4, 7, 4),
            Cursor = Cursors.Hand, Focusable = false,
        };
        b.Click += (s, e) => { act(); e.Handled = true; };
        return b;
    }

    private void SetTool(int t)
    {
        _tool = t;
        Cursor = t == 1 ? Cursors.Pen : t == 2 ? Cursors.Cross : Cursors.Arrow;
        RefreshToolPanel();
    }

    private void RefreshToolPanel()
    {
        if (_toolPanel?.Child is not StackPanel sp) return;
        foreach (var el in sp.Children)
        {
            if (el is Button b && b.Tag is int t)
            {
                b.Background = t == _tool
                    ? new SolidColorBrush(Color.FromRgb(0x2E, 0xB8, 0x72))
                    : Brushes.Transparent;
            }
            if (el is Border d && d.Tag is Color c)
            {
                d.BorderBrush = c == _penColor
                    ? new SolidColorBrush(Color.FromRgb(0x5C, 0xE8, 0xA0))
                    : new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
                d.BorderThickness = c == _penColor ? new Thickness(2.5) : new Thickness(1);
                d.Width = d.Height = c == _penColor ? 19 : 16;
            }
        }
    }

    /// <summary>工具条贴「窗口右下角」（DIP）。</summary>
    private void PositionToolPanel()
    {
        if (_toolPanel == null) return;
        _toolPanel.Measure(new Size(Math.Max(1, _layers.ActualWidth), Math.Max(1, _layers.ActualHeight)));
        double tw = _toolPanel.DesiredSize.Width, th = _toolPanel.DesiredSize.Height;
        double x = Math.Max(4, _layers.ActualWidth - tw - 6);
        double y = Math.Max(4, _layers.ActualHeight - th - 6);
        _toolPanel.SetValue(Canvas.LeftProperty, x);
        _toolPanel.SetValue(Canvas.TopProperty, y);
    }

    private void UndoStroke()
    {
        if (_strokes.Count == 0) return;
        var last = _strokes[^1];
        _ink.Children.Remove(last);
        _strokes.RemoveAt(_strokes.Count - 1);
    }

    // ==================== 鼠标 ====================

    private void OnDown(object s, MouseButtonEventArgs e)
    {
        try { Focusable = true; Focus(); } catch { }
        if (IsFromToolPanel(e)) { e.Handled = false; return; }   // 工具条内部按钮自理

        var p = e.GetPosition(_ink);
        if (_tool == 1)
        {
            _strokeActive = true; _strokeAnchor = p;
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
        else if (_tool == 2)
        {
            _strokeActive = true; _strokeAnchor = p;
            _currentRect = new Rectangle
            {
                Stroke = new SolidColorBrush(_penColor),
                StrokeThickness = _penW,
                StrokeDashCap = PenLineCap.Round,
                Fill = Brushes.Transparent,   // 纯框（标注惯用）
            };
            _ink.Children.Add(_currentRect);
        }
        else
        {
            // 拖动移动（物理像素）
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
        if (_strokeActive)
        {
            var p = e.GetPosition(_ink);
            if (_tool == 1 && _currentStroke != null)
            {
                if ((p - _strokeAnchor).Length > 1.2)
                {
                    _currentStroke.Points.Add(p);
                    _strokeAnchor = p;
                }
            }
            else if (_tool == 2 && _currentRect != null)
            {
                double x = Math.Min(p.X, _strokeAnchor.X), y = Math.Min(p.Y, _strokeAnchor.Y);
                double w = Math.Abs(p.X - _strokeAnchor.X), h = Math.Abs(p.Y - _strokeAnchor.Y);
                _currentRect.SetValue(Canvas.LeftProperty, x);
                _currentRect.SetValue(Canvas.TopProperty, y);
                _currentRect.Width = w; _currentRect.Height = h;
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
        if (_strokeActive)
        {
            if (_tool == 1)
            {
                if (_currentStroke != null)
                {
                    if (_currentStroke.Points.Count > 1) _strokes.Add(_currentStroke);
                    else _ink.Children.Remove(_currentStroke);
                }
                _currentStroke = null;
            }
            else if (_tool == 2)
            {
                if (_currentRect != null)
                {
                    if (_currentRect.Width > 2 && _currentRect.Height > 2) _strokes.Add(_currentRect);
                    else _ink.Children.Remove(_currentRect);
                }
                _currentRect = null;
            }
            _strokeActive = false;
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
        // 有绘制工具时：滚轮调粗细；无工具：缩放
        if (_tool is 1 or 2)
        {
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

    private async void ShowThicknessBubble(Point at)
    {
        try
        {
            if (_thickBubble == null)
            {
                _thickBubble = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1E)),
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3),
                    IsHitTestVisible = false,
                    Child = new TextBlock { Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold },
                };
            }
            if (_thickBubble.Parent is Panel p2) p2.Children.Remove(_thickBubble);
            if (_thickBubble.Child is TextBlock tb) tb.Text = $"{_penW:0.#} px";
            _uiCanvas.Children.Add(_thickBubble);
            _thickBubble.Measure(new Size(Math.Max(1, _layers.ActualWidth), Math.Max(1, _layers.ActualHeight)));
            _thickBubble.SetValue(Canvas.LeftProperty, Math.Max(4, Math.Min(at.X + 14, _layers.ActualWidth - _thickBubble.DesiredSize.Width - 4)));
            _thickBubble.SetValue(Canvas.TopProperty, Math.Max(4, at.Y - _thickBubble.DesiredSize.Height - 8));
            await Task.Delay(800);
            if (_thickBubble?.Parent is Panel p3) p3.Children.Remove(_thickBubble);
        }
        catch { }
    }

    // ==================== 菜单 / 输出 ====================

    private ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        var undo = new MenuItem { Header = "撤销一笔（Ctrl+Z）" }; undo.Click += (_, _) => UndoStroke();
        var copy = new MenuItem { Header = "复制（含标注）" }; copy.Click += (_, _) => CopyToClipboard();
        var save = new MenuItem { Header = "保存…" }; save.Click += (_, _) => Save();
        var scale1 = new MenuItem { Header = "缩放 100%" }; scale1.Click += (_, _) => SetScale(1.0, null);
        var close = new MenuItem { Header = "关闭" }; close.Click += (_, _) => Close();
        m.Items.Add(undo); m.Items.Add(new Separator());
        m.Items.Add(copy); m.Items.Add(save); m.Items.Add(scale1); m.Items.Add(new Separator()); m.Items.Add(close);
        return m;
    }

    /// <summary>合成「底图 + 标注」（隐藏工具条后渲染 _layers——不含装饰层）。</summary>
    private RenderTargetBitmap? Compose()
    {
        try
        {
            var w = _layers.ActualWidth > 1 ? _layers.ActualWidth : _baseW;
            var h = _layers.ActualHeight > 1 ? _layers.ActualHeight : _baseH;
            var dpi = _dpi > 0.01 ? _dpi : 1.0;
            _layers.Measure(new Size(w, h));
            _layers.Arrange(new Rect(0, 0, w, h));
            _layers.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)Math.Round(w * dpi), (int)Math.Round(h * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            rtb.Render(_layers);
            rtb.Freeze();
            return rtb;
        }
        catch { return null; }
    }

    private void CopyToClipboard()
    {
        try
        {
            if (_strokes.Count == 0) Clipboard.SetImage(_src);
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
        PositionToolPanel();
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