using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DeskBuddy.Models;
using DeskBuddy.Services;
using PathIO = System.IO.Path;

namespace DeskBuddy.Tools;

/// <summary>连连看：ComfyUI 式节点画布。节点自由摆放，端口间拖线连接（多入多出）。</summary>
public partial class MindmapWindow : Window
{
    private CvDoc _doc = new();
    private string? _path;
    private bool _dirty;
    private double _zoom = 1.0;
    private readonly Dictionary<string, Border> _nodeCards = new();
    private readonly Dictionary<string, Ellipse> _portDots = new();   // portId -> 圆点
    private readonly Dictionary<string, FrameworkElement> _portOwners = new(); // portId -> 归属节点卡片

    // 撤销
    private readonly Stack<CvDoc> _undo = new();
    private readonly Stack<CvDoc> _redo = new();

    // 画布
    private bool _panning; private Point _panStart;
    // 节点拖拽
    private Border? _dragCard; private bool _moved;
    // 连线
    private Ellipse? _dragPort; private Point _linkCur; private bool _linking;
    private string? _selNodeId, _selLinkId;
    private bool _suppressProp;

    private static readonly string _filter = "连连看图 (*.llk)|*.llk|JSON (*.json)|*.json|所有文件 (*.*)|*.*";

    public MindmapWindow()
    {
        InitializeComponent();
        ApplyTheme();
        InitSwatches();
        NewDocument();
        CanvasHost.Loaded += (_, _) => CenterView();
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    /// <summary>macOS System 色系。</summary>
    private static readonly Color SysBlue = Color.FromRgb(0x0A, 0x84, 0xFF);
    private static readonly Color SysGreen = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color SysOrange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private static readonly Color SysRed = Color.FromRgb(0xFF, 0x45, 0x3A);
    private static readonly Color SysPurple = Color.FromRgb(0xBF, 0x5A, 0xF2);

    private void ApplyTheme()
    {
        var isDark = Theme.From(Services.ConfigManager.Load().Theme).IsDark;
        // 采用 macOS 质感配色（与主主题解耦，保证连连看始终是精致苹果风）
        if (isDark)
        {
            Resources["TextPrimary"] = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF7));
            Resources["TextSecondary"] = Frozen(Color.FromRgb(0x98, 0x9A, 0xA5));
            Resources["CardBorder"] = Frozen(Color.FromArgb(0x2C, 0xFF, 0xFF, 0xFF));
            Resources["BtnBgHover"] = Frozen(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
            Resources["CardBg"] = Frozen(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1E));   // 近不透明白灰
            Resources["CanvasBg"] = Frozen(Color.FromArgb(0xF2, 0x17, 0x17, 0x1A)); // 画布稍深
            Resources["ContextBg"] = Frozen(Color.FromArgb(0xE6, 0x28, 0x28, 0x2B));
        }
        else
        {
            Resources["TextPrimary"] = Frozen(Color.FromRgb(0x1D, 0x1D, 0x1F));
            Resources["TextSecondary"] = Frozen(Color.FromRgb(0x6E, 0x6E, 0x73));
            Resources["CardBorder"] = Frozen(Color.FromArgb(0x30, 0x00, 0x00, 0x00));
            Resources["BtnBgHover"] = Frozen(Color.FromArgb(0x14, 0x00, 0x00, 0x00));
            Resources["CardBg"] = Frozen(Color.FromArgb(0xE8, 0xF5, 0xF5, 0xF7));
            Resources["CanvasBg"] = Frozen(Color.FromArgb(0xF0, 0xE8, 0xE8, 0xEC));
            Resources["ContextBg"] = Frozen(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF));
        }
        // 画板背景如果缺失则用 CardBg 派生
        if (!Resources.Contains("CanvasBg")) Resources["CanvasBg"] = Resources["CardBg"];
    }
    private void InitSwatches() { foreach (var s in new[] { Swatch1, Swatch2, Swatch3, Swatch4, Swatch5 }) if (s != null) try { s.Background = new SolidColorBrush(ColorFromHex((string)s.Tag)); } catch { } }
    private Color ColorFromHex(string hex) { try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return Color.FromArgb(0x22, 0x4A, 0x90, 0xFF); } }

    // ==================== 变换核心（已验证数学） ====================
    private Point HostToCanvas(Point h) => new Point((h.X - PanTf.X) / _zoom, (h.Y - PanTf.Y) / _zoom);
    private void CenterView() { PanTf.X = CanvasHost.ActualWidth / 2; PanTf.Y = CanvasHost.ActualHeight / 2; }
    private void SetZoom(double z, Point focus)
    {
        z = Math.Clamp(z, 0.25, 3.0); if (Math.Abs(z - _zoom) < 1e-4) return;
        var c = HostToCanvas(focus); _zoom = z;
        ZoomTf.ScaleX = ZoomTf.ScaleY = _zoom;
        PanTf.X = focus.X - c.X * _zoom; PanTf.Y = focus.Y - c.Y * _zoom;
        ZoomText.Text = (int)(_zoom * 100) + "%";
    }
    private void OnPreviewMouseWheel(object s, MouseWheelEventArgs e) { SetZoom(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), e.GetPosition(CanvasHost)); e.Handled = true; }
    private void OnZoomIn(object s, RoutedEventArgs e) => SetZoom(_zoom * 1.2, Center());
    private void OnZoomOut(object s, RoutedEventArgs e) => SetZoom(_zoom / 1.2, Center());
    private Point Center() => new Point(CanvasHost.ActualWidth / 2, CanvasHost.ActualHeight / 2);

    // ==================== 文档 ====================
    public void NewDocument()
    {
        BeforeChange(); _doc = new CvDoc(); _path = null; _dirty = false;
        var a = new CvNode { Title = "开始", X = 0, Y = 0, Color = "#EE30D158" };
        var b = new CvNode { Title = "处理", X = 320, Y = 120, Color = "#EE0A84FF" };
        var c = new CvNode { Title = "输出", X = 640, Y = 0, Color = "#EEFF9F0A" };
        a.Outputs.Add(new CvPort()); b.Inputs.Add(new CvPort()); b.Outputs.Add(new CvPort()); c.Inputs.Add(new CvPort());
        _doc.Nodes.AddRange(new[] { a, b, c });
        _doc.Links.Add(new CvLink { FromPort = a.Outputs[0].Id, ToPort = b.Inputs[0].Id });
        _doc.Links.Add(new CvLink { FromPort = b.Outputs[0].Id, ToPort = c.Inputs[0].Id });
        Rebuild(); UpdateTitle();
    }
    private void LoadPath(string path) { _doc = CvDoc.Load(path); _path = path; _dirty = false; Rebuild(); UpdateTitle(); AddRecent(path); }
    private void OnNew(object s, RoutedEventArgs e) => EnsureSaved(NewDocument);
    private void OnOpen(object s, RoutedEventArgs e) => EnsureSaved(() => { var d = new Microsoft.Win32.OpenFileDialog { Filter = _filter, Title = "打开" }; if (d.ShowDialog(this) == true) LoadPath(d.FileName); });
    private void OnSave(object s, RoutedEventArgs e) => Save();
    private void OnClose(object s, RoutedEventArgs e) => EnsureSaved(Close);
    private void Save() { if (_path == null) ChooseDirAndSave(); else { _doc.Save(_path); _dirty = false; AddRecent(_path); UpdateTitle(); } }
    private void ChooseDirAndSave()
    {
        var cfg = ConfigManager.Load();
        if (string.IsNullOrWhiteSpace(cfg.MindmapDir) || !Directory.Exists(cfg.MindmapDir!)) { var f = new Microsoft.Win32.OpenFolderDialog { Title = "选择保存目录（首次使用）" }; if (f.ShowDialog(this) != true) return; cfg.MindmapDir = f.FolderName; ConfigManager.Save(cfg); }
        var d = new Microsoft.Win32.SaveFileDialog { Filter = _filter, Title = "保存", InitialDirectory = cfg.MindmapDir };
        if (d.ShowDialog(this) != true) return;
        var file = PathIO.GetExtension(d.FileName).ToLowerInvariant() == ".llk" ? d.FileName : d.FileName + ".llk";
        _doc.Save(file); _path = file; _dirty = false; AddRecent(file); UpdateTitle();
    }
    private void EnsureSaved(Action c) { if (!_dirty) { c(); return; } var r = MessageBox.Show(this, "有未保存的修改，是否保存？", "连连看", MessageBoxButton.YesNoCancel, MessageBoxImage.Question); if (r == MessageBoxResult.Yes) { Save(); c(); } else if (r == MessageBoxResult.No) c(); }
    private void UpdateTitle() { DocNameText.Text = _path == null ? "未命名" : PathIO.GetFileName(_path); Title = "🌳 连连看 — " + DocNameText.Text; }
    private void AddRecent(string file) { try { var c = ConfigManager.Load(); c.RecentMindmaps.Remove(file); c.RecentMindmaps.Insert(0, file); if (c.RecentMindmaps.Count > 20) c.RecentMindmaps.RemoveRange(20, c.RecentMindmaps.Count - 20); ConfigManager.Save(c); } catch { } }

    // ==================== 渲染 ====================
    private void Rebuild()
    {
        NodeCanvas.Children.Clear(); LinkCanvas.Children.Clear(); OverlayCanvas.Children.Clear();
        _nodeCards.Clear(); _portDots.Clear(); _portOwners.Clear();
        _portDots.Clear();
        foreach (var n in _doc.Nodes) AddNodeCard(n);
        RedrawLinks(); RefreshProps();
    }
    private void AddNodeCard(CvNode n)
    {
        // macOS 质感卡片：半透明底色 + 顶部细腻高光渐变 + 极柔和投影
        var grad = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), 0),   // 顶部高光
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.4)
        }, new Point(0, 0), new Point(0, 1));
        var baseSolid = ColorFromHex(n.Color); // 基色（半透明）
        var bg = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(0xEE, baseSolid.R, baseSolid.G, baseSolid.B), 0),
            new GradientStop(Color.FromArgb(0xD9, (byte)(baseSolid.R * 0.8), (byte)(baseSolid.G * 0.8), (byte)(baseSolid.B * 0.8)), 1)
        }, new Point(0, 0), new Point(0, 1));
        var card = new Border
        {
            Tag = n, Background = bg, CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Padding = new Thickness(0), MinWidth = 80
        };
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect
        { Color = Colors.Black, Opacity = 0.28, BlurRadius = 10, ShadowDepth = 1.5, Direction = 270 };
        Canvas.SetLeft(card, n.X); Canvas.SetTop(card, n.Y); Canvas.SetZIndex(card, 10);
        var root = new StackPanel();
        // 标题
        var title = new TextBlock { Text = n.Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(14, 10, 14, 6), TextWrapping = TextWrapping.Wrap, MaxWidth = 220 };
        root.Children.Add(title);
        // 端口行：左输入，右输出
        var rows = Math.Max(Math.Max(n.Inputs.Count, n.Outputs.Count), 1);
        for (int i = 0; i < rows; i++)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (i < n.Inputs.Count)
            {
                var p = n.Inputs[i];
                var dot = MakeDot(Brushes.White, i == 0); // 输入端口（空心）
                Grid.SetColumn(dot, 0);
                var lbl = new TextBlock { Margin = new Thickness(2, 0, 0, 0) };
                row.Children.Add(dot); var t1 = PortLabel(p, "进"); Grid.SetColumn(t1, 1); t1.Margin = new Thickness(6,0,0,0); row.Children.Add(t1);
                RegisterPort(p.Id, dot, card);
            }
            if (i < n.Outputs.Count)
            {
                var p = n.Outputs[i];
                var dot = MakeDot(new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xFF)), true);
                Grid.SetColumn(dot, 2);
                row.Children.Insert(0, dot); // 放最左避免错列
                var lbl = PortLabel(p, "出"); Grid.SetColumn(lbl, 1); lbl.HorizontalAlignment = HorizontalAlignment.Right; lbl.Margin = new Thickness(0,0,6,0);
                RegisterPort(p.Id, dot, card);
            }
            // 保证行有点占位
            root.Children.Add(row);
            // 用固定行高撑起
            row.Height = 26;
        }
        // 实际端口对齐：输入左、输出右 —— 简化：用一个边界 margin
        card.Child = root;
        // 调整宽度
        card.Measure(new Size(300, 200)); n.W = Math.Max(120, card.DesiredSize.Width + 8); card.Width = n.W;
        card.MouseLeftButtonDown += (s, e) => { SelectNode(n.Id); DragCardStart(card, e); };
        card.MouseMove += (s, e) => DragCardMove(card, e);
        card.MouseLeftButtonUp += (s, e) => DragCardEnd(card, e);
        _nodeCards[n.Id] = card;
        NodeCanvas.Children.Add(card);
    }
    private Ellipse MakeDot(Brush fill, bool solid) => new Ellipse { Width = 14, Height = 14, Fill = fill, Stroke = Brushes.Black, StrokeThickness = 1, StrokeDashArray = solid ? null : new DoubleCollection { 2, 1 }, Cursor = Cursors.Cross };
    private TextBlock PortLabel(CvPort p, string def) => new TextBlock { Text = string.IsNullOrEmpty(p.Name) ? def : p.Name, FontSize = 11, Foreground = Brushes.White };
    private void RegisterPort(string pid, Ellipse dot, Border card)
    {
        _portDots[pid] = dot; _portOwners[pid] = card;
        // 输入端口：按住它不能拖出线，只有输出端口能拖出。但这里统一允许右端输出拖线。
        dot.MouseLeftButtonDown += (s, e) =>
        {
            var isOutput = IsOutputPort(pid);
            if (!isOutput) return;
            _dragPort = dot; _linkCur = GetPortCenter(dot); _linking = true; CanvasHost.CaptureMouse();
            RedrawLinkPreview(); e.Handled = true;
        };
    }
    private bool IsOutputPort(string pid)
    {
        foreach (var n in _doc.Nodes) if (n.Outputs.Any(p => p.Id == pid)) return true;
        return false;
    }
    private Point GetPortCenter(Ellipse dot)
    {
        // 圆点实际在卡片内，用其相对 NodeCanvas 位置
        var p = dot.TranslatePoint(new Point(dot.Width / 2, dot.Height / 2), NodeCanvas);
        return p;
    }
    private void RedrawLinks()
    {
        OverlayCanvas.Children.Clear();
        LinkCanvas.Children.Clear();
        foreach (var l in _doc.Links)
        {
            if (!_portDots.TryGetValue(l.FromPort, out var f) || !_portDots.TryGetValue(l.ToPort, out var t)) continue;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(GetPortCenter(f), false, false);
                var mx = (GetPortCenter(f).X + GetPortCenter(t).X) / 2;
                ctx.BezierTo(new Point(mx, GetPortCenter(f).Y), new Point(mx, GetPortCenter(t).Y), GetPortCenter(t), true, false);
            }
            g.Freeze();
            var ph = new System.Windows.Shapes.Path { Stroke = (_selLinkId == l.Id) ? new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x9A, 0xA2, 0xB0, 0xB8)), StrokeThickness = 2, Data = g, Tag = l.Id, Cursor = Cursors.Hand };
            ph.MouseLeftButtonDown += (s, e) => { _selLinkId = l.Id; _selNodeId = null; RefreshProps(); RedrawLinks(); e.Handled = true; };
            LinkCanvas.Children.Add(ph);
        }
    }
    private void RedrawLinkPreview()
    {
        OverlayCanvas.Children.Clear();
        if (_dragPort == null) return;
        var ph = new System.Windows.Shapes.Path { Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0x4A, 0x90, 0xFF)), StrokeThickness = 2.2, StrokeDashArray = new DoubleCollection { 4, 3 }, Data = new LineGeometry(GetPortCenter(_dragPort), _linkCur) };
        OverlayCanvas.Children.Add(ph);
    }

    // ==================== 画布事件 ====================
    private void OnCanvasMouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            // 中键拖拽平移画布
            _panning = true; _panStart = e.GetPosition(CanvasHost); CanvasHost.CaptureMouse(); e.Handled = true; return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        SelectNode(null); SelectLink(null);
        _panning = true; _panStart = e.GetPosition(CanvasHost); CanvasHost.CaptureMouse(); e.Handled = true;
    }
    private void OnCanvasMouseMove(object s, MouseEventArgs e)
    {
        if (_linking && _dragPort != null) { _linkCur = e.GetPosition(NodeCanvas); RedrawLinkPreview(); }
        else if (_panning) { var h = e.GetPosition(CanvasHost); PanTf.X += (h.X - _panStart.X) / _zoom; PanTf.Y += (h.Y - _panStart.Y) / _zoom; _panStart = h; }
    }
    private void OnCanvasMouseUp(object s, MouseButtonEventArgs e)
    {
        if (_linking && _dragPort != null)
        {
            var drop = HitTestPort(e.GetPosition(NodeCanvas));
            var fromNode = _portOwners.TryGetValue(PortIdOf(_dragPort), out var fo) ? NodeOf(fo) : null;
            if (drop != null && !ReferenceEquals(_dragPort, drop))
            {
                var toPortId = PortIdOf(drop);
                var isInput = !IsOutputPort(toPortId);
                if (isInput)
                {
                    BeforeChange(); _doc.Links.Add(new CvLink { FromPort = PortIdOf(_dragPort), ToPort = toPortId }); _dirty = true;
                }
            }
            _linking = false; _dragPort = null; _linkStartIsBlank = false; CanvasHost.ReleaseMouseCapture(); RedrawLinks();
        }
        _linking = false; _dragPort = null; _panning = false; CanvasHost.ReleaseMouseCapture(); RedrawLinks();
    }
    private bool _linkStartIsBlank;
    private void OnCanvasMouseLeave(object s, MouseEventArgs e) { if (_linking) { _linking = false; _dragPort = null; RedrawLinks(); } }

    private string PortIdOf(Ellipse dot) => _portDots.FirstOrDefault(x => ReferenceEquals(x.Value, dot)).Key ?? "";
    private CvNode? NodeOf(FrameworkElement fe) { var c = fe.Tag as CvNode; foreach (var n in _doc.Nodes) if (ReferenceEquals(n, c)) return c; return _nodeCards.FirstOrDefault(x => ReferenceEquals(x.Value, fe)).Value != null ? (CvNode)((Border)fe).Tag : null; }
    private Ellipse? HitTestPort(Point c)
    {
        double best = 30, bd = double.MaxValue; Ellipse? hit = null;
        foreach (var kv in _portDots)
        {
            var center = GetPortCenter(kv.Value);
            var d = (center - c).Length;
            if (d < best) { best = d; hit = kv.Value; }
        }
        return hit;
    }

    // ==================== 节点拖拽 ====================
    private void DragCardStart(Border card, MouseButtonEventArgs e) { _dragCard = card; _moved = false; card.CaptureMouse(); e.Handled = true; }
    private void DragCardMove(Border card, MouseEventArgs e)
    {
        if (_dragCard == card && e.LeftButton == MouseButtonState.Pressed)
        {
            var c = e.GetPosition(NodeCanvas);
            var n = (CvNode)card.Tag;
            if ((c - CanvasGetAt(card)).Length > 3) _moved = true;
            Canvas.SetLeft(card, c.X - card.ActualWidth / 2); Canvas.SetTop(card, c.Y - 20);
            n.X = Canvas.GetLeft(card); n.Y = Canvas.GetTop(card);
            RedrawLinks(); _dirty = true;
        }
    }
    private Point CanvasGetAt(Border card) { var p = card.TranslatePoint(new Point(0, 0), NodeCanvas); return p; }
    private void DragCardEnd(Border card, MouseButtonEventArgs e) { if (_dragCard == card) _dragCard = null; card.ReleaseMouseCapture(); _moved = false; }

    // ==================== 选择 / 属性 ====================
    private void SelectNode(string? id) { _selNodeId = id; _selLinkId = null; foreach (var kv in _nodeCards) kv.Value.BorderBrush = kv.Key == id ? new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)); RefreshProps(); RedrawLinks(); }
    private void SelectLink(string? id) { _selLinkId = id; _selNodeId = null; RefreshProps(); RedrawLinks(); }
    private void RefreshProps()
    {
        _suppressProp = true;
        try
        {
            if (_selNodeId != null) { var n = _doc.Nodes.FirstOrDefault(x => x.Id == _selNodeId); if (n != null) { PropTitle.Text = "节点属性"; PropText.Text = n.Title; PropThickness.Value = 1; } }
            else if (_selLinkId != null) { PropTitle.Text = "连线属性"; PropText.Text = ""; PropThickness.Value = 2; }
            else { PropTitle.Text = "属性"; PropText.Text = ""; }
        }
        finally { _suppressProp = false; }
    }
    private void OnPropTextChanged(object s, TextChangedEventArgs e)
    {
        if (_suppressProp || _selNodeId == null) return;
        var n = _doc.Nodes.FirstOrDefault(x => x.Id == _selNodeId); if (n == null) return;
        if (n.Title != PropText.Text) { BeforeChange(); n.Title = PropText.Text; _dirty = true; var card = _findCard(n.Id); if (card != null && card.Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock tb) tb.Text = n.Title; }
    }
    private Border? _findCard(string id) => _nodeCards.TryGetValue(id, out var c) ? c : null;
    private void OnPickColor(object s, RoutedEventArgs e) { if (_selNodeId == null) return; var n = _doc.Nodes.FirstOrDefault(x => x.Id == _selNodeId); if (n == null) return; BeforeChange(); n.Color = (string)((Button)s).Tag; if (_nodeCards.TryGetValue(n.Id, out var c)) c.Background = new SolidColorBrush(ColorFromHex(n.Color)); _dirty = true; }
    private void OnCycleColor(object s, RoutedEventArgs e) { if (_selNodeId == null) return; var n = _doc.Nodes.FirstOrDefault(x => x.Id == _selNodeId); if (n == null) return; var cols = new[] { "#EE0A84FF", "#EEFF453A", "#EE30D158", "#EEFF9F0A", "#EEBF5AF2" }; var i = Array.IndexOf(cols, n.Color); var idx = i < 0 ? 0 : (i + 1) % cols.Length; BeforeChange(); n.Color = cols[idx]; if (_nodeCards.TryGetValue(n.Id, out var c)) c.Background = new SolidColorBrush(ColorFromHex(n.Color)); _dirty = true; }

    // ==================== 撤销 / 快捷键 / 导出 ====================
    private void BeforeChange() { _undo.Push(CloneDoc(_doc)); if (_undo.Count > 60) { var a = _undo.ToArray(); Array.Reverse(a); _undo.Clear(); foreach (var x in a.Take(59)) _undo.Push(x); } _redo.Clear(); }
    private static CvDoc CloneDoc(CvDoc d) => new CvDoc { Nodes = d.Nodes.Select(x => new CvNode { Id = x.Id, Title = x.Title, X = x.X, Y = x.Y, Color = x.Color, W = x.W, Inputs = x.Inputs.Select(p => new CvPort { Id = p.Id, Name = p.Name }).ToList(), Outputs = x.Outputs.Select(p => new CvPort { Id = p.Id, Name = p.Name }).ToList() }).ToList(), Links = d.Links.Select(x => new CvLink { Id = x.Id, FromPort = x.FromPort, ToPort = x.ToPort }).ToList() };
    private void Undo() { if (_undo.Count == 0) return; _redo.Push(CloneDoc(_doc)); _doc = _undo.Pop(); _dirty = true; Rebuild(); }
    private void Redo() { if (_redo.Count == 0) return; _undo.Push(CloneDoc(_doc)); _doc = _redo.Pop(); _dirty = true; Rebuild(); }
    private void OnUndo(object s, RoutedEventArgs e) => Undo();
    private void OnRedo(object s, RoutedEventArgs e) => Redo();

    private void OnToolNode(object s, RoutedEventArgs e) => AddNodeAt(CanvasCenterCanvas());
    private void OnArrowChanged(object s, RoutedEventArgs e) { }
    private void OnThicknessChanged(object s, RoutedPropertyChangedEventArgs<double> e) { }

    private void OnWindowKeyDown(object s, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Z) { Undo(); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Y) { Redo(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { _linking = false; _dragPort = null; SelectNode(null); SelectLink(null); e.Handled = true; return; }
        if (e.Key == Key.N) { AddNodeAt(CanvasCenterCanvas()); e.Handled = true; }
        if (e.Key == Key.Delete) { if (_selNodeId != null) DeleteNode(_selNodeId); if (_selLinkId != null) DeleteLink(_selLinkId); e.Handled = true; }
    }
    private Point CanvasCenterCanvas() => HostToCanvas(Center());
    private void AddNodeAt(Point c) { BeforeChange(); var n = new CvNode { Title = "节点", X = c.X - 90, Y = c.Y - 15, Color = "#EE0A84FF" }; n.Inputs.Add(new CvPort()); n.Outputs.Add(new CvPort()); _doc.Nodes.Add(n); Rebuild(); _dirty = true; SelectNode(n.Id); }
    private void DeleteNode(string id) { var n = _doc.Nodes.FirstOrDefault(x => x.Id == id); if (n == null) return; BeforeChange(); var ps = n.Inputs.Select(p => p.Id).Concat(n.Outputs.Select(p => p.Id)).ToHashSet(); _doc.Nodes.Remove(n); _doc.Links.RemoveAll(x => ps.Contains(x.FromPort) || ps.Contains(x.ToPort)); _selNodeId = null; Rebuild(); _dirty = true; }
    private void DeleteLink(string id) { BeforeChange(); _doc.Links.RemoveAll(x => x.Id == id); _selLinkId = null; RedrawLinks(); _dirty = true; }

    private void OnExport(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图片 (*.png)|*.png", Title = "导出图片" };
        if (d.ShowDialog(this) != true) return;
        try { var w = Math.Max(1, (int)(CanvasHost.ActualWidth + Math.Abs(PanTf.X) * 2)); var h = Math.Max(1, (int)(CanvasHost.ActualHeight + Math.Abs(PanTf.Y) * 2)); var rt = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); rt.Render(CanvasHost); var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rt)); using var fs = File.Create(d.FileName); enc.Save(fs); MessageBox.Show(this, "已导出：" + PathIO.GetFileName(d.FileName), "导出"); } catch (Exception ex) { MessageBox.Show(this, "导出失败：" + ex.Message, "错误"); }
    }

    // ==================== 窗口 resize ====================
    private bool _resizing; private Vector _rs; private double _rw, _rh;
    private void OnResizeGripDown(object s, MouseButtonEventArgs e) { _resizing = true; _rs = (Vector)e.GetPosition(this); _rw = Width; _rh = Height; ResizeGrip.CaptureMouse(); e.Handled = true; }
    private void OnWindowResizeMove(object s, MouseEventArgs e) { if (!_resizing || e.LeftButton != MouseButtonState.Pressed) return; var d = (Vector)e.GetPosition(this) - _rs; var wa = SystemParameters.WorkArea; Width = Math.Clamp(_rw + d.X, 900, wa.Width); Height = Math.Clamp(_rh + d.Y, 540, wa.Height); e.Handled = true; }
    private void OnResizeGripUp(object s, MouseButtonEventArgs e) { _resizing = false; ResizeGrip.ReleaseMouseCapture(); e.Handled = true; }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { if (_dirty) { var r = MessageBox.Show(this, "有未保存的修改，是否保存？", "连连看", MessageBoxButton.YesNoCancel, MessageBoxImage.Question); if (r == MessageBoxResult.Yes) Save(); else if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; } } base.OnClosing(e); }
}