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
    private readonly Canvas _uiCanvas = new();   // 图片区装饰（气泡提示）
    private readonly Grid _layers;        // 图片区（图+笔迹）
    private readonly Grid _root;          // 根：图片区 + 底部工具停靠区两行
    private readonly Canvas _dock;        // 底部停靠区（工具条常驻）
    private double _dockH = 46;
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
        _layers.Children.Add(_uiCanvas);      // 第3层：装饰（气泡，点定位绝不遮挡）
        _layers.SizeChanged += (_, _) => PositionToolPanel();
        SizeChanged += (_, _) => PositionToolPanel();   // 窗口尺寸变化（缩放）也重摆

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        PreviewMouseRightButtonDown += OnRightDown;   // 绘制中右键=取消（先于 ContextMenu）
        PreviewMouseRightButtonUp += (s2, e2) =>
        {
            // 菜单在右键【抬起】时绽放——绘制中吞掉，杜绝「取消+菜单同时出现」
            if (_tool is 1 or 2) { e2.Handled = true; }
        };
        MouseWheel += OnWheel;
        MouseDoubleClick += (_, _) => Close();
        PreviewKeyDown += OnKey;
        // 关键：窗口显式 DIP 尺寸 + 底部工具停靠区（工具条在贴图【外侧】右下角）
        // 布局教训：Grid 的 Star 行会被 Image 自然尺寸(物理像素)撑爆——改【显式行高】：
        // 行1 = 选区高 h（Image 恰好填满），行2 = DockH 停靠带，窗口总高 = h + DockH（精确）
        const double DockH = 46;
        _dock = new Canvas { IsHitTestVisible = true };
        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h) });      // 图片区（显式高）
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DockH) });  // 停靠区
        Grid.SetRow(_layers, 0);
        _root.Children.Add(_layers);
        Grid.SetRow(_dock, 1);
        _root.Children.Add(_dock);
        _host.Child = _root;
        Content = _host;

        Width = Math.Max(24, w);
        Height = Math.Max(24, h + DockH);
        Left = x; Top = y;

        Loaded += (_, _) =>
        {
            Focusable = true; Focus();
            _dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (_dpi <= 0.01) _dpi = 1.0;
            _baseW = w; _baseH = h;
            _dockH = DockH;
            _px = (int)Math.Round(x * _dpi); _py = (int)Math.Round(y * _dpi);
            _pw = (int)Math.Round(w * _dpi); _ph = (int)Math.Round((h + DockH) * _dpi);
            DebugLog.Write($"[PIN] loaded: W={Width:F0}xH{Height:F0} dpi={_dpi} src={src.PixelWidth}x{src.PixelHeight} pr={_pw}x{_ph}");
            BuildToolPanel();   // 常驻工具条（停靠区）
            // 布局完成后再摆一次（Loaded 时 ActualHeight 未定 → 工具条 startling 位置错）
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                PositionToolPanel();
                DebugLog.Write($"[PIN] toolbar final: dock={_dock.ActualWidth:F0}x{_dock.ActualHeight:F0}");
            }));
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
        // ★Button 的 Click 路由在本窗口（多层 Preview 拦截+CaptureMouse）下不可靠——
        //   换【Border+MouseLeftButtonDown 直挂】（色板同款，用户实测有效）
        for (int t = 0; t <= 2; t++)
        {
            int tt = t;   // 闭包独立捕获
            var glyph = t == 0 ? ToolNone : t == 1 ? ToolPen : ToolRect;
            var tip = t == 0 ? "移动/缩放（1）" : t == 1 ? "画笔（2）" : "矩形（3）";
            var b = new Border
            {
                Child = new TextBlock { Text = glyph, FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)) },
                Padding = new Thickness(8, 4, 8, 4),
                CornerRadius = new CornerRadius(7),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = t,
                ToolTip = tip,
            };
            b.MouseLeftButtonDown += (s2, e2) => { SetTool(tt); e2.Handled = true; };
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
        // 撤销（Border 直挂，同色板方案）
        var undo = new Border
        {
            Child = new TextBlock { Text = "↩", FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)) },
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(7),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "撤销一笔（Ctrl+Z）",
        };
        undo.MouseLeftButtonDown += (s2, e2) => { UndoStroke(); e2.Handled = true; };
        sp.Children.Add(undo);

        _toolPanel.Child = sp;
        // 工具条放底部停靠区（贴图外）右下角
        _dock.Children.Add(_toolPanel);
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

    private void SetTool(int t)
    {
        DebugLog.Write($"[PIN] SetTool {t}");
        _tool = t;
        Cursor = t == 1 ? Cursors.Pen : t == 2 ? Cursors.Cross : Cursors.Arrow;
        RefreshToolPanel();
    }

    private void RefreshToolPanel()
    {
        if (_toolPanel?.Child is not StackPanel sp) return;
        foreach (var el in sp.Children)
        {
            // 工具按钮（Border）
            if (el is Border b && b.Tag is int t)
            {
                b.Background = t == _tool
                    ? new SolidColorBrush(Color.FromRgb(0x2E, 0xB8, 0x72))
                    : Brushes.Transparent;
            }
            // 色板点（Border + Color Tag）
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

    /// <summary>工具条贴「底部停靠区」右侧（贴图方框外·水平右对齐·带内垂直居中）。</summary>
    private void PositionToolPanel()
    {
        if (_toolPanel == null) return;
        double w = ActualWidth > 1 ? ActualWidth : Width;
        // 停靠带实际高度（布局后）；未布局时用声明值
        double dockH = _dock.ActualHeight > 1 ? _dock.ActualHeight : (_root.RowDefinitions.Count > 1 ? _root.RowDefinitions[1].Height.Value : _dockH);
        _toolPanel.Measure(new Size(Math.Max(1, w), Math.Max(1, dockH)));
        double tw = _toolPanel.DesiredSize.Width, th = _toolPanel.DesiredSize.Height;
        double x = Math.Max(4, w - tw - 8);          // 右对齐
        double y = Math.Max(2, (dockH - th) / 2);    // 停靠带内垂直居中
        // 兜底：th>带高（异常）时贴带顶
        if (th + 4 > dockH) y = 2;
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
        var fromTb = IsFromToolPanel(e);
        DebugLog.Write($"[PIN] down: fromTool={fromTb} tool={_tool} src={e.OriginalSource?.GetType().Name}");
        if (fromTb) { e.Handled = false; return; }   // 工具条内部按钮自理

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

    /// <summary>右击（绘制中）：取消当前未完成的笔画/矩形（丢弃半成品，不退出工具）。
    /// 右击（非绘制中）：回到移动工具。</summary>
    private void OnRightDown(object s, MouseButtonEventArgs e)
    {
        if (IsFromToolPanelRight(e)) { e.Handled = false; return; }
        if (_strokeActive)
        {
            // 丢弃正在画的一半
            if (_currentStroke != null) { _ink.Children.Remove(_currentStroke); _currentStroke = null; }
            if (_currentRect != null) { _ink.Children.Remove(_currentRect); _currentRect = null; }
            _strokeActive = false;
            ReleaseMouseCapture();
            DebugLog.Write("[PIN] right-click: draft cancelled");
        }
        else if (_tool != 0)
        {
            SetTool(0);   // 退出绘制工具，回移动
            DebugLog.Write("[PIN] right-click: back to move tool");
        }
        e.Handled = true;
    }

    private bool IsFromToolPanelRight(MouseButtonEventArgs e) => IsFromToolPanel(e);

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

    /// <summary>右键打开菜单前拦截：绘制中右键语义=取消（OnRightDown 已消化）。</summary>
    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        if (_tool is 1 or 2)
        {
            e.Handled = true;   // 绘制工具激活时不弹菜单（右键专属取消）
            return;
        }
        base.OnContextMenuOpening(e);
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
        int newH = (int)Math.Round((_baseH + _dockH) * _dpi * s);   // 含底部停靠区
        if (newW < 24 || newH < 24 || newW > 8000 || newH > 8000) return;
        double ax = anchor?.ax ?? 0.5, ay = anchor?.ay ?? 0.5;
        int apx = _px + (int)Math.Round(_pw * ax);
        int apy = _py + (int)Math.Round(_ph * ay);
        _scale = s;
        MovePhys(apx - (int)Math.Round(newW * ax), apy - (int)Math.Round(newH * ay), newW, newH);
        // WPF DIP 尺寸同步（与 SetWindowPos 协作不冲突：窗口宽高变了触发布局自适应）
        Width = _baseW * s;
        Height = (_baseH + _dockH) * s;
    }
}