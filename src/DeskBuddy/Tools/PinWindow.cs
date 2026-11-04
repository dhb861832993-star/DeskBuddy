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
using System.Windows.Threading;
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
    private int _tool = 0;                // 0=无(拖动/缩放) 1=画笔 2=矩形 3=箭头 4=文字
    private Color _penColor = Color.FromRgb(0xFF, 0x3B, 0x30);   // 默认红
    private double _penW = 3.0;
    private const double PenWMin = 1, PenWMax = 24;
    private double _fontSize = 20;        // 文字字号（DIP）
    private const double FontMin = 10, FontMax = 96;

    // 绘制中
    private bool _strokeActive;
    private Point _strokeAnchor;
    private Polyline? _currentStroke;
    private Rectangle? _currentRect;
    private System.Windows.Shapes.Path? _currentArrow;          // 箭头（Path：线杆 + 填充三角头）
    private TextBox? _editBox;            // 正在输入的文字框
    private Color _editColor;             // 输入框起笔时的颜色（改色不影响已开始的这段）
    private TextBlock? _editingBlock;     // 正在二次编辑的既有文字
    private readonly List<UIElement> _strokes = new();

    // ===== 既有标注的选中 / 拖动 =====
    private UIElement? _hitEl;            // 按下时命中的标注
    private Point _hitDown;               // 按下点（_ink 坐标）
    private bool _hitMoved;               // 是否已经拖动（用来区分“单击=编辑”和“拖动=位移”）
    private double _moveL, _moveT;        // 文字的原始 Canvas 位置
    private Point _moveA0, _moveB0;       // 箭头的原始端点

    // ===== 悬停反馈（告诉用户这里能拖）=====
    private UIElement? _hoverEl;          // 当前悬停的标注
    private Rectangle? _hoverRect;        // 悬停虚线框（画在 _dock，不进输出）

    /// <summary>箭头端点（存在 Path.Tag），移动时整体平移。</summary>
    private sealed class ArrowInfo { public Point A, B; }

    private Border? _toolPanel;
    private Window? _toolHost;            // 工具条独立悬浮窗（挂在贴图右下外侧）
    private System.Windows.Threading.DispatcherTimer? _toolOwnerGuard;
    private Border? _thickBubble;

    // 物理像素状态
    private int _px, _py, _pw, _ph;
    private double _dpi = 1.0;
    private bool _dragging; private Point _dragStart; private int _dragWinPX, _dragWinPY;

    private const string ToolNone = "✥", ToolPen = "✏", ToolRect = "▭", ToolArrow = "↗", ToolText = "A";

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
            if (_tool is 1 or 2 or 3 or 4) { e2.Handled = true; }
        };
        MouseWheel += OnWheel;
        MouseLeave += (_, _) => { ShowHover(null); Cursor = ToolCursor; };
        MouseDoubleClick += (_, _) => Close();
        PreviewKeyDown += OnKey;
        // 工具条设计：不占独立停靠行（会形成一条白横杠很丑）——
        // 直接【悬浮在图片右下角】（半压住底边、右对齐），无边框带、视觉挂在角上
        const double DockH = 0;    // 不再使用独立停靠行
        _dock = new Canvas { IsHitTestVisible = true, Background = Brushes.Transparent };
        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h) });   // 只有图片区
        Grid.SetRow(_layers, 0);
        _root.Children.Add(_layers);
        _root.Children.Add(_dock);   // dock 叠在图片上（无行），Canvas 内容点定位
        _host.Child = _root;
        Content = _host;

        Width = Math.Max(24, w);
        Height = Math.Max(24, h);   // 窗口高度 = 纯图片高（工具条悬浮不出窗口）
        Left = x; Top = y;

        Loaded += (_, _) =>
        {
            Focusable = true; Focus();
            _dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (_dpi <= 0.01) _dpi = 1.0;
            _baseW = w; _baseH = h;
            _dockH = 0;
            _px = (int)Math.Round(x * _dpi); _py = (int)Math.Round(y * _dpi);
            _pw = (int)Math.Round(w * _dpi); _ph = (int)Math.Round(h * _dpi);
            DebugLog.Write($"[PIN] loaded: W={Width:F0}xH{Height:F0} dpi={_dpi} src={src.PixelWidth}x{src.PixelHeight} pr={_pw}x{_ph}");
            BuildToolPanel();   // 悬浮工具条（右下角内侧）
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                PositionToolPanel();
            }));
        };
        ContextMenu = BuildMenu();
    }

    private void OnKey(object s, KeyEventArgs e)
    {
        // 正在输入文字：只有 Esc/Enter 归我们，其余按键必须交给输入框（含 IME、方向键）
        if (_editBox != null)
        {
            if (e.Key == Key.Escape) { CancelText(); e.Handled = true; return; }
            if (e.Key == Key.Enter) { CommitText(); e.Handled = true; return; }
            return;
        }
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { CopyToClipboard(); e.Handled = true; return; }
        if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { UndoStroke(); e.Handled = true; return; }
        // 1/2/3/4/5 快捷选工具
        if (e.Key == Key.D1) { SetTool(0); e.Handled = true; return; }
        if (e.Key == Key.D2) { SetTool(1); e.Handled = true; return; }
        if (e.Key == Key.D3) { SetTool(2); e.Handled = true; return; }
        if (e.Key == Key.D4) { SetTool(3); e.Handled = true; return; }
        if (e.Key == Key.D5) { SetTool(4); e.Handled = true; return; }
        // 颜色：Alt+1..6（原来占用 4~9，被新工具让出来）
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && e.Key >= Key.D1 && e.Key <= Key.D6)
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

    // ==================== 常驻工具条（右下角） ====================

    /// <summary>构建标注工具条：工具三选（拖动/画笔/矩形）+ 色板 + 撤销。
    /// 防重入（Visual 只能有一个父级——重复构建曾致 argc 崩溃）。</summary>
    private void BuildToolPanel()
    {
        if (_toolHost != null) return;   // 已建过：跳过
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

        // 工具按钮：拖动 / 画笔 / 矩形 / 箭头 / 文字
        // ★Button 的 Click 路由在本窗口（多层 Preview 拦截+CaptureMouse）下不可靠——
        //   换【Border+MouseLeftButtonDown 直挂】（色板同款，用户实测有效）
        for (int t = 0; t <= 4; t++)
        {
            int tt = t;   // 闭包独立捕获
            var glyph = t switch { 1 => ToolPen, 2 => ToolRect, 3 => ToolArrow, 4 => ToolText, _ => ToolNone };
            var tip = t switch
            {
                1 => "画笔（2）",
                2 => "矩形（3）",
                3 => "箭头（4）· 滚轮调粗细",
                4 => "文字（5）· 滚轮调字号",
                _ => "移动/缩放（1）· 点已有文字或箭头可拖动，单击文字可改字",
            };
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
        // 工具条 = 独立悬浮子窗口（挂在贴图右下角【外侧正下方】，无白条、跟随移动/缩放）
        _toolHost = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Background = Brushes.Transparent,
            AllowsTransparency = true,   // 小窗口无位图渲染问题（只有原生控件）
            Content = _toolPanel,
            SizeToContent = SizeToContent.WidthAndHeight,
        };
        _toolHost.Show();
        _toolOwnerGuard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _toolOwnerGuard.Tick += (_, _) => FollowToolHost();
        _toolOwnerGuard.Start();
        RefreshToolPanel();
        PositionToolPanel();
    }

    /// <summary>工具条跟随贴图窗口（右下角外侧正下方 DIP，Owner 联动）。</summary>
    private void FollowToolHost()
    {
        if (_toolHost == null || !_toolHost.IsLoaded) return;
        try
        {
            _toolHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tw = _toolPanel?.DesiredSize.Width ?? 280, th = _toolPanel?.DesiredSize.Height ?? 37;
            // 贴图窗口 DIP 右下 → 工具条贴其正下方右对齐
            _toolHost.Left = Left + Width - tw - 10;
            _toolHost.Top = Top + Height + 6;      // 外侧 6px 间距
        }
        catch { }
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
        CommitText();                         // 切工具先把正在输入的文字落下
        ShowHover(null);
        _tool = t;
        Cursor = ToolCursor;
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

    /// <summary>同 FollowToolHost（SizeChanged/Loaded 回调入口）。</summary>
    private void PositionToolPanel() => FollowToolHost();

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
        if (_editBox != null)
        {
            if (IsFromEditBox(e)) return;            // 点在输入框内：交给 TextBox 挪光标/选字
            CommitText();                            // 点别处 = 提交本次输入
            e.Handled = true;
            return;
        }

        var p = e.GetPosition(_ink);
        ShowHover(null);   // 按下就收起悬停提示（开始画/拖了）

        // 先看有没有点中已有标注（文字/箭头）：拖动=位移，单击文字=再次编辑。
        // 画笔/矩形模式不参与（要能直接在上面画）。
        if (CanGrabAnnotations)
        {
            var hit = HitAnnotation(p);
            if (hit != null)
            {
                StartHit(hit, p);
                CaptureMouse();
                e.Handled = true;
                return;
            }
        }

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
        else if (_tool == 3)
        {
            _strokeActive = true; _strokeAnchor = p;
            _currentArrow = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(_penColor),
                Fill = new SolidColorBrush(_penColor),   // 只填三角头（线杆那条 figure 标了 isFilled=false）
                StrokeThickness = _penW,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Flat,      // 平头收在三角底边，粗线不会戳出尖端
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
                Data = ArrowPathGeometry(p, p, _penW),
                Tag = new ArrowInfo { A = p, B = p },
            };
            _ink.Children.Add(_currentArrow);
        }
        else if (_tool == 4)
        {
            StartText(p);
            e.Handled = true;
            return;    // 文字不需要捕获鼠标（要能连续输入）
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

    /// <summary>事件源是否在文字输入框内。</summary>
    private bool IsFromEditBox(MouseButtonEventArgs e)
    {
        if (_editBox == null) return false;
        var d = e.OriginalSource as DependencyObject;
        while (d != null)
        {
            if (ReferenceEquals(d, _editBox)) return true;
            try { d = System.Windows.Media.VisualTreeHelper.GetParent(d); } catch { return false; }
        }
        return false;
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
        if (_hitEl != null) { MoveHit(e.GetPosition(_ink)); e.Handled = true; return; }
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
            else if (_tool == 3 && _currentArrow != null)
            {
                _currentArrow.Data = ArrowPathGeometry(_strokeAnchor, p, _penW);
                if (_currentArrow.Tag is ArrowInfo ci) ci.B = p;
            }
            e.Handled = true;
            return;
        }
        if (!_dragging) { UpdateHover(e.GetPosition(_ink)); return; }
        var cur = GetCursorPosPhys();
        MovePhys(_dragWinPX + (int)Math.Round(cur.X - _dragStartScreen.X),
                _dragWinPY + (int)Math.Round(cur.Y - _dragStartScreen.Y),
                _pw, _ph);
        e.Handled = true;
    }

    private void OnUp(object s, MouseButtonEventArgs e)
    {
        // 已有标注：拖动结束 / 单击文字进入编辑
        if (_hitEl != null)
        {
            var el = _hitEl;
            var moved = _hitMoved;
            _hitEl = null; _hitMoved = false;
            ReleaseMouseCapture();
            if (!moved && el is TextBlock tb) EditText(tb);   // 没拖动 = 单击 → 改字
            e.Handled = true;
            return;
        }
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
            else if (_tool == 3)
            {
                if (_currentArrow != null)
                {
                    var g = _currentArrow.Data.Bounds;
                    // 太短的当误触丢掉（Bounds 是包围盒，用它判断比算长度省事）
                    if (Math.Max(g.Width, g.Height) > 8) _strokes.Add(_currentArrow);
                    else _ink.Children.Remove(_currentArrow);
                }
                _currentArrow = null;
            }
            _strokeActive = false;
        }
        else
        {
            _dragging = false;
        }
        ReleaseMouseCapture();
        Cursor = ToolCursor;    // 拖完恢复工具光标，下一次移动会重新判定悬停
        e.Handled = true;
    }

    /// <summary>右击（绘制中）：取消当前未完成的笔画/矩形（丢弃半成品，不退出工具）。
    /// 右击（非绘制中）：回到移动工具。</summary>
    private void OnRightDown(object s, MouseButtonEventArgs e)
    {
        if (IsFromToolPanelRight(e)) { e.Handled = false; return; }
        if (_editBox != null) { CancelText(); e.Handled = true; return; }   // 输入中右键 = 丢弃这段文字
        if (_strokeActive)
        {
            // 丢弃正在画的一半
            if (_currentStroke != null) { _ink.Children.Remove(_currentStroke); _currentStroke = null; }
            if (_currentRect != null) { _ink.Children.Remove(_currentRect); _currentRect = null; }
            if (_currentArrow != null) { _ink.Children.Remove(_currentArrow); _currentArrow = null; }
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
        // 文字工具：滚轮调字号（输入中也能实时调）
        if (_tool == 4)
        {
            double nf = Math.Clamp(_fontSize + (e.Delta > 0 ? 2 : -2), FontMin, FontMax);
            _fontSize = nf;
            if (_editBox != null) _editBox.FontSize = nf;
            ShowBubble(e.GetPosition(_ink), $"{nf:0} px");
            e.Handled = true;
            return;
        }
        // 画笔/矩形/箭头：滚轮调粗细
        if (_tool is 1 or 2 or 3)
        {
            var nw = _penW + (e.Delta > 0 ? 0.5 : -0.5);
            _penW = Math.Clamp(nw, PenWMin, PenWMax);
            ShowBubble(e.GetPosition(_ink), $"{_penW:0.#} px");
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

    private async void ShowBubble(Point at, string text)
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
            if (_thickBubble.Child is TextBlock tb) tb.Text = text;
            _uiCanvas.Children.Add(_thickBubble);
            _thickBubble.Measure(new Size(Math.Max(1, _layers.ActualWidth), Math.Max(1, _layers.ActualHeight)));
            _thickBubble.SetValue(Canvas.LeftProperty, Math.Max(4, Math.Min(at.X + 14, _layers.ActualWidth - _thickBubble.DesiredSize.Width - 4)));
            _thickBubble.SetValue(Canvas.TopProperty, Math.Max(4, at.Y - _thickBubble.DesiredSize.Height - 8));
            await Task.Delay(800);
            if (_thickBubble?.Parent is Panel p3) p3.Children.Remove(_thickBubble);
        }
        catch { }
    }

    // ==================== 箭头 / 文字 ====================

    /// <summary>箭头几何：线杆只画到三角底边（粗线不会从尖端戳出来），头部随线宽变大。</summary>
    private static (Point ShaftEnd, Point[] Head) ArrowGeo(Point s, Point e, double thickness)
    {
        double dx = e.X - s.X, dy = e.Y - s.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.001) return (e, Array.Empty<Point>());
        double ux = dx / len, uy = dy / len;
        double headLen = Math.Min(Math.Max(10, 18 + thickness * 2), len * 0.45);
        double halfW = Math.Max(headLen * 0.45, thickness * 1.6);
        var bc = new Point(e.X - ux * headLen, e.Y - uy * headLen);
        var head = new[]
        {
            e,
            new Point(bc.X - uy * halfW, bc.Y + ux * halfW),
            new Point(bc.X + uy * halfW, bc.Y - ux * halfW),
        };
        double overlap = Math.Min(headLen * 0.2, thickness * 0.5);
        return (new Point(bc.X + ux * overlap, bc.Y + uy * overlap), head);
    }

    private static Geometry ArrowPathGeometry(Point s, Point e, double thickness)
    {
        var (shaftEnd, head) = ArrowGeo(s, e, thickness);
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            ctx.BeginFigure(s, false, false);          // 线杆：不填充
            ctx.LineTo(shaftEnd, true, false);
            if (head.Length == 3)
            {
                ctx.BeginFigure(head[0], true, true);   // 三角头：填充 + 闭合
                ctx.LineTo(head[1], true, false);
                ctx.LineTo(head[2], true, false);
            }
        }
        sg.Freeze();
        return sg;
    }

    /// <summary>在落点起一个文字输入框（Enter 提交 / Esc 取消 / 点别处提交）。</summary>
    private void StartText(Point p)
    {
        CommitText();
        _editColor = _penColor;
        OpenEditor(p.X, p.Y, "", _fontSize);
    }

    /// <summary>点中已有文字 → 原地再编辑（内容、字号、颜色都能改）。</summary>
    private void EditText(TextBlock block)
    {
        CommitText();
        _editingBlock = block;
        _editColor = (block.Foreground as SolidColorBrush)?.Color ?? _penColor;
        block.Visibility = Visibility.Hidden;    // 先藏起来，避免和输入框重影
        OpenEditor(Canvas.GetLeft(block), Canvas.GetTop(block), block.Text, block.FontSize);
        DebugLog.Write("[PIN] text re-edit: " + block.Text);
    }

    /// <summary>建输入框并抢焦点（新建与二次编辑共用）。</summary>
    private void OpenEditor(double left, double top, string text, double fontSize)
    {
        var tb = new TextBox
        {
            Text = text,
            FontFamily = new FontFamily("Microsoft YaHei"),
            FontSize = fontSize,
            Foreground = new SolidColorBrush(_editColor),
            CaretBrush = new SolidColorBrush(_editColor),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0, 2, 0),
            MinWidth = 70,
            AcceptsReturn = false,
            Focusable = true,
        };
        if (double.IsNaN(left)) left = 8;
        if (double.IsNaN(top)) top = 8;
        Canvas.SetLeft(tb, left); Canvas.SetTop(tb, top);
        _ink.Children.Add(tb);
        _editBox = tb;
        try
        {
            Focusable = true; Focus(); tb.Focus(); Keyboard.Focus(tb);
            tb.CaretIndex = tb.Text.Length;
        }
        catch { }
        // 兜底抢焦点（窗口激活可能把焦点吞掉）
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            try { if (ReferenceEquals(_editBox, tb)) { tb.Focus(); Keyboard.Focus(tb); } } catch { }
        }));
        DebugLog.Write("[PIN] text edit start");
    }

    private void CommitText()
    {
        var tb = _editBox; if (tb == null) return;
        _editBox = null;
        var txt = (tb.Text ?? "").Trim();
        double lx = Canvas.GetLeft(tb), ly = Canvas.GetTop(tb);
        double fs = tb.FontSize;
        _ink.Children.Remove(tb);

        // 二次编辑：改回原来那个 TextBlock（清空 = 删掉这条）
        var editing = _editingBlock;
        _editingBlock = null;
        if (editing != null)
        {
            if (txt.Length == 0) { _ink.Children.Remove(editing); _strokes.Remove(editing); return; }
            editing.Text = txt;
            editing.FontSize = fs;
            editing.Foreground = new SolidColorBrush(_editColor);
            Canvas.SetLeft(editing, lx); Canvas.SetTop(editing, ly);
            editing.Visibility = Visibility.Visible;
            DebugLog.Write($"[PIN] text update: {txt}");
            return;
        }

        if (txt.Length == 0) return;
        var block = new TextBlock
        {
            Text = txt,
            FontFamily = new FontFamily("Microsoft YaHei"),
            FontSize = fs,
            Foreground = new SolidColorBrush(_editColor),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(block, lx); Canvas.SetTop(block, ly);
        _ink.Children.Add(block);
        _strokes.Add(block);
        DebugLog.Write($"[PIN] text commit: {txt}");
    }

    private void CancelText()
    {
        var tb = _editBox; if (tb == null) return;
        _editBox = null;
        _ink.Children.Remove(tb);
        if (_editingBlock != null) { _editingBlock.Visibility = Visibility.Visible; _editingBlock = null; }
        DebugLog.Write("[PIN] text cancel");
    }

    // ==================== 已有标注的命中 / 拖动 ====================

    /// <summary>文字的可拖动/可单击范围：比文字本身大一圈，方便在左下角这种边角位置抓到。</summary>
    private static Rect HitBoxOf(TextBlock tb)
    {
        double l = Canvas.GetLeft(tb), t = Canvas.GetTop(tb);
        if (double.IsNaN(l) || double.IsNaN(t)) return Rect.Empty;
        double padX = Math.Max(12, tb.FontSize * 0.5);
        double padY = Math.Max(10, tb.FontSize * 0.55);
        var r = new Rect(l, t, Math.Max(1, tb.ActualWidth), Math.Max(1, tb.ActualHeight));
        r.Inflate(padX, padY);
        return r;
    }

    /// <summary>标注的视觉包围盒（用于悬停虚线框与命中高亮）。</summary>
    private static Rect BoundsOf(UIElement el)
    {
        if (el is TextBlock tb)
        {
            double l = Canvas.GetLeft(tb), t = Canvas.GetTop(tb);
            if (double.IsNaN(l) || double.IsNaN(t)) return Rect.Empty;
            return new Rect(l, t, Math.Max(1, tb.ActualWidth), Math.Max(1, tb.ActualHeight));
        }
        if (el is System.Windows.Shapes.Path pa && pa.Data != null)
        {
            var b = pa.Data.Bounds;
            if (!b.IsEmpty) b.Inflate(2, 2);
            return b;
        }
        return Rect.Empty;
    }

    /// <summary>点中哪个已有标注（文字按放大后的包围盒，箭头按到线段的距离）。后加的优先。</summary>
    private UIElement? HitAnnotation(Point p)
    {
        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            var el = _strokes[i];
            if (el is TextBlock tb && tb.Visibility == Visibility.Visible)
            {
                var hit = HitBoxOf(tb);
                if (!hit.IsEmpty && hit.Contains(p)) return tb;
            }
            else if (el is System.Windows.Shapes.Path pa && pa.Tag is ArrowInfo ai)
            {
                // 线越粗越好点，另外再给 8px 容差
                if (DistToSegment(p, ai.A, ai.B) <= Math.Max(10, pa.StrokeThickness * 1.2 + 8)) return pa;
            }
        }
        return null;
    }

    private static double DistToSegment(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 < 0.0001) return (p - a).Length;
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return (p - new Point(a.X + t * dx, a.Y + t * dy)).Length;
    }

    private void StartHit(UIElement el, Point p)
    {
        ShowHover(null);                       // 开始拖了就不用再提示
        _hitEl = el; _hitDown = p; _hitMoved = false;
        if (el is TextBlock tb) { _moveL = Canvas.GetLeft(tb); _moveT = Canvas.GetTop(tb); }
        else if (el is System.Windows.Shapes.Path pa && pa.Tag is ArrowInfo ai) { _moveA0 = ai.A; _moveB0 = ai.B; }
        Cursor = Cursors.SizeAll;
    }

    // ==================== 悬停反馈 ====================

    /// <summary>当前工具是否允许“抓已有标注”（画笔/矩形模式下点在标注上是画画，不提示）。</summary>
    private bool CanGrabAnnotations => _tool is 0 or 3 or 4;

    private Cursor ToolCursor => _tool switch
    {
        1 => Cursors.Pen,
        2 or 3 => Cursors.Cross,
        4 => Cursors.IBeam,
        _ => Cursors.Arrow,
    };

    /// <summary>空闲移动时更新悬停提示：命中就画虚线框 + 移动光标。</summary>
    private void UpdateHover(Point p)
    {
        if (!CanGrabAnnotations || _editBox != null) { ShowHover(null); return; }
        var hit = HitAnnotation(p);
        ShowHover(hit);
        Cursor = hit != null ? Cursors.SizeAll : ToolCursor;
    }

    /// <summary>在 _dock（不参与合成输出）上画/收悬停虚线框。</summary>
    private void ShowHover(UIElement? el)
    {
        if (ReferenceEquals(_hoverEl, el)) return;
        _hoverEl = el;
        if (el == null)
        {
            if (_hoverRect != null) _hoverRect.Visibility = Visibility.Collapsed;
            return;
        }
        var b = BoundsOf(el);
        if (b.IsEmpty) { if (_hoverRect != null) _hoverRect.Visibility = Visibility.Collapsed; return; }

        _hoverRect ??= new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x5C, 0xE8, 0xA0)),
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        _hoverRect.Width = Math.Max(1, b.Width);
        _hoverRect.Height = Math.Max(1, b.Height);
        Canvas.SetLeft(_hoverRect, b.X);
        Canvas.SetTop(_hoverRect, b.Y);
        if (_hoverRect.Parent is Panel pp) pp.Children.Remove(_hoverRect);
        _dock.Children.Add(_hoverRect);
        _hoverRect.Visibility = Visibility.Visible;
    }

    /// <summary>拖动已有标注：文字改 Canvas 位置，箭头整体平移两端点。</summary>
    private void MoveHit(Point p)
    {
        if (_hitEl == null) return;
        double dx = p.X - _hitDown.X, dy = p.Y - _hitDown.Y;
        if (!_hitMoved && Math.Abs(dx) + Math.Abs(dy) < 3) return;   // 3px 内仍算“单击”
        _hitMoved = true;
        if (_hitEl is TextBlock tb)
        {
            Canvas.SetLeft(tb, _moveL + dx);
            Canvas.SetTop(tb, _moveT + dy);
        }
        else if (_hitEl is System.Windows.Shapes.Path pa && pa.Tag is ArrowInfo ai)
        {
            ai.A = new Point(_moveA0.X + dx, _moveA0.Y + dy);
            ai.B = new Point(_moveB0.X + dx, _moveB0.Y + dy);
            pa.Data = ArrowPathGeometry(ai.A, ai.B, pa.StrokeThickness);
        }
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
        if (_tool is 1 or 2 or 3 or 4 || _editBox != null)
        {
            e.Handled = true;   // 绘制/输入文字时不弹菜单（右键专属取消）
            return;
        }
        base.OnContextMenuOpening(e);
    }

    /// <summary>贴图关闭时收掉工具条子窗与跟随定时器。</summary>
    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _toolOwnerGuard?.Stop();
            _toolHost?.Close();
        }
        catch { }
        base.OnClosed(e);
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
            CommitText();   // 正在输入的文字先落下，避免复制漏掉
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
        CommitText();
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
        FollowToolHost();   // 工具条立即跟随（拖动流畅）
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
        // WPF DIP 尺寸 + 图片行高同步
        Width = _baseW * s;
        Height = _baseH * s;
        if (_root.RowDefinitions.Count > 0)
        {
            _root.RowDefinitions[0].Height = new GridLength(_baseH * s);
        }
        FollowToolHost();
    }
}