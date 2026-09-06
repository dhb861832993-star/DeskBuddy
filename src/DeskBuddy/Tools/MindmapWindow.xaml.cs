using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

/// <summary>连连看：Monet 风格通用节点工作流编辑器。</summary>
public partial class MindmapWindow : Window
{
    private WorkflowDoc _doc = new();
    private string? _path;
    private bool _dirty;
    private double _zoom = 1.0;

    private readonly Dictionary<string, Border> _nodeCards = new();
    private readonly Dictionary<string, Ellipse> _portDots = new();
    private readonly Dictionary<string, Border> _portOwners = new();

    private readonly Stack<WorkflowDoc> _undo = new();
    private readonly Stack<WorkflowDoc> _redo = new();

    private bool _panning; private Point _panStart;
    private Border? _dragCard; private double _grabOffX, _grabOffY;
    private Ellipse? _dragPort; private Point _linkCur; private bool _linking;
    private string? _selNodeId; private string? _selLinkId;
    private bool _suppressProp;
    private DateTime _lastBlankDown = DateTime.MinValue; private Point _lastBlankPt;
    private DateTime _lastClick = DateTime.MinValue;

    private bool _running;
    private CancellationTokenSource? _execCts;

    private static readonly string _filter = "连连看图 (*.llk)|*.llk|JSON (*.json)|*.json|所有文件 (*.*)|*.*";

    // ==================== 节点类型库 ====================
    private sealed class NodeDef
    {
        public string Title = ""; public string Kind = NodeKind.Process; public string Color = "#FF3A3A3C";
        public (string Name, string Type)[] Inputs = Array.Empty<(string, string)>();
        public (string Name, string Type)[] Outputs = Array.Empty<(string, string)>();
        public (string Key, string Label, string Control, string Default)[] Params = Array.Empty<(string, string, string, string)>();
    }

    private static readonly NodeDef[] NodeLibrary =
    {
        new NodeDef { Title = "文本输入", Kind = NodeKind.Input, Inputs = Array.Empty<(string,string)>(), Outputs = new[] { ("文本", PortType.Text) }, Params = new[] { ("value", "内容", "text", "你好") } },
        new NodeDef { Title = "数值输入", Kind = NodeKind.Input, Outputs = new[] { ("数值", PortType.Number) }, Params = new[] { ("value", "数值", "number", "0") } },
        new NodeDef { Title = "图片输入", Kind = NodeKind.Input, Outputs = new[] { ("图片", PortType.Image) }, Params = new[] { ("url", "图片路径", "text", "") } },
        new NodeDef { Title = "文本参数", Kind = NodeKind.Parameter, Outputs = new[] { ("文本", PortType.Text) }, Params = new[] { ("value", "文本", "text", "默认文本") } },
        new NodeDef { Title = "数值参数", Kind = NodeKind.Parameter, Outputs = new[] { ("数值", PortType.Number) }, Params = new[] { ("value", "数值", "number", "0") } },
        new NodeDef { Title = "滑杆参数", Kind = NodeKind.Parameter, Outputs = new[] { ("数值", PortType.Number) }, Params = new[] { ("value", "数值", "slider", "50") } },
        new NodeDef { Title = "开关", Kind = NodeKind.Parameter, Outputs = new[] { ("布尔", PortType.Number) }, Params = new[] { ("value", "开启", "toggle", "false") } },
        new NodeDef { Title = "下拉选择", Kind = NodeKind.Parameter, Outputs = new[] { ("文本", PortType.Text) }, Params = new[] { ("value", "选项", "dropdown", "选项A") } },
        new NodeDef { Title = "文本处理", Kind = NodeKind.Process, Inputs = new[] { ("文本", PortType.Text) }, Outputs = new[] { ("结果", PortType.Text) }, Params = new[] { ("op", "操作", "dropdown", "大写") } },
        new NodeDef { Title = "数值计算", Kind = NodeKind.Process, Inputs = new[] { ("A", PortType.Number), ("B", PortType.Number) }, Outputs = new[] { ("结果", PortType.Number) }, Params = new[] { ("op", "运算", "dropdown", "加") } },
        new NodeDef { Title = "图片处理", Kind = NodeKind.Process, Inputs = new[] { ("图片", PortType.Image) }, Outputs = new[] { ("图片", PortType.Image) }, Params = new[] { ("op", "处理", "dropdown", "裁剪") } },
        new NodeDef { Title = "文本输出", Kind = NodeKind.Output, Inputs = new[] { ("文本", PortType.Text) } },
        new NodeDef { Title = "图片输出", Kind = NodeKind.Output, Inputs = new[] { ("图片", PortType.Image) } },
        new NodeDef { Title = "分支", Kind = NodeKind.Process, Inputs = new[] { ("输入", PortType.Any) }, Outputs = new[] { ("真", PortType.Any), ("假", PortType.Any) }, Params = new[] { ("cond", "条件", "text", "true") } },
    };

    public MindmapWindow()
    {
        InitializeComponent();
        InitSwatches();
        InitNodeLibrary();
        NewDocument();
        CanvasHost.Loaded += (_, _) => OnFitView(null, null);
    }

    private void InitSwatches() { foreach (var s in new[] { Swatch1, Swatch2, Swatch3, Swatch4, Swatch5 }) if (s != null) { try { s.Background = new SolidColorBrush(ColorFromHex((string)s.Tag)); } catch { } } }
    private Color ColorFromHex(string hex) { try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return Color.FromRgb(0x3A, 0x3A, 0x3C); } }

    // ==================== 节点库 ====================
    private void InitNodeLibrary()
    {
        NodeLibPanel.Children.Clear();
        foreach (var def in NodeLibrary)
        {
            var btn = new Button { Content = def.Title, Tag = def, Style = (Style)FindResource("LibItem"), Margin = new Thickness(0, 0, 0, 6) };
            btn.Click += (s, e) => AddNodeFromDef((NodeDef)((Button)s).Tag!);
            NodeLibPanel.Children.Add(btn);
        }
    }
    private void AddNodeFromDef(NodeDef def)
    {
        var n = new WfNode { Title = def.Title, Kind = def.Kind, Color = def.Color };
        foreach (var (nm, t) in def.Inputs) n.Inputs.Add(new Port { Name = nm, Type = t, IsInput = true });
        foreach (var (nm, t) in def.Outputs) n.Outputs.Add(new Port { Name = nm, Type = t });
        foreach (var (k, lbl, ctrl, dv) in def.Params) n.Params[k] = dv;
        var c = CanvasCenter();
        n.X = c.X - 90; n.Y = c.Y - 40;
        BeforeChange(); _doc.Nodes.Add(n); Rebuild(); _dirty = true; SelectNode(n.Id);
    }
    private void OnToolAdd(object s, RoutedEventArgs e) => AddNodeFromDef(NodeLibrary.First(x => x.Title == "文本处理"));

    // ==================== 变换核心 ====================
    private Point HostToCanvas(Point h) => new Point((h.X - PanTf.X) / _zoom, (h.Y - PanTf.Y) / _zoom);
    private Point CanvasCenter() => HostToCanvas(new Point(CanvasHost.ActualWidth / 2, CanvasHost.ActualHeight / 2));
    private void CenterView() { PanTf.X = CanvasHost.ActualWidth / 2; PanTf.Y = CanvasHost.ActualHeight / 2; }
    private void SetZoom(double z, Point focus)
    {
        z = Math.Clamp(z, 0.2, 3.0); if (Math.Abs(z - _zoom) < 1e-4) return;
        var c = HostToCanvas(focus); _zoom = z;
        ZoomTf.ScaleX = ZoomTf.ScaleY = _zoom;
        PanTf.X = focus.X - c.X * _zoom; PanTf.Y = focus.Y - c.Y * _zoom;
        ZoomText.Text = (int)(_zoom * 100) + "%";
    }
    private void OnPreviewMouseWheel(object s, MouseWheelEventArgs e) { SetZoom(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), e.GetPosition(CanvasHost)); e.Handled = true; }
    private void OnZoomIn(object s, RoutedEventArgs e) => SetZoom(_zoom * 1.2, new Point(CanvasHost.ActualWidth / 2, CanvasHost.ActualHeight / 2));
    private void OnZoomOut(object s, RoutedEventArgs e) => SetZoom(_zoom / 1.2, new Point(CanvasHost.ActualWidth / 2, CanvasHost.ActualHeight / 2));

    private void OnFitView(object s, RoutedEventArgs e)
    {
        if (_doc.Nodes.Count == 0) { CenterView(); return; }
        double minX = _doc.Nodes.Min(x => x.X), minY = _doc.Nodes.Min(x => x.Y);
        double maxX = _doc.Nodes.Max(x => x.X + Math.Max(x.W, 170));
        double maxY = _doc.Nodes.Max(x => x.Y + 110);
        double pad = 80;
        double viewW = Math.Max(200, CanvasHost.ActualWidth - pad * 2);
        double viewH = Math.Max(150, CanvasHost.ActualHeight - pad * 2);
        double scale = Math.Min(viewW / Math.Max(maxX - minX, 60), viewH / Math.Max(maxY - minY, 60));
        scale = Math.Clamp(scale, 0.2, 1.4);
        _zoom = scale; ZoomTf.ScaleX = ZoomTf.ScaleY = scale;
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        PanTf.X = CanvasHost.ActualWidth / 2 - cx * scale;
        PanTf.Y = CanvasHost.ActualHeight / 2 - cy * scale;
        ZoomText.Text = (int)(_zoom * 100) + "%";
    }

    // ==================== 文档 ====================
    public void NewDocument() { _doc = WorkflowDoc.NewSample(); _path = null; _dirty = false; Rebuild(); UpdateTitle(); }
    private void LoadPath(string p) { _doc = WorkflowDoc.Load(p); _path = p; _dirty = false; Rebuild(); UpdateTitle(); AddRecent(p); }
    private void OnNew(object s, RoutedEventArgs e) => EnsureSaved(NewDocument);
    private void OnOpen(object s, RoutedEventArgs e) => EnsureSaved(() => { var d = new Microsoft.Win32.OpenFileDialog { Filter = _filter, Title = "打开" }; if (d.ShowDialog(this) == true) LoadPath(d.FileName); });
    private void OnSave(object s, RoutedEventArgs e) => Save();
    private void OnClose(object s, RoutedEventArgs e) => EnsureSaved(Close);
    private void Save() { if (_path == null) ChooseDirAndSave(); else { _doc.Save(_path); _dirty = false; AddRecent(_path); UpdateTitle(); } }
    private void ChooseDirAndSave()
    {
        var cfg = ConfigManager.Load();
        if (string.IsNullOrWhiteSpace(cfg.MindmapDir) || !Directory.Exists(cfg.MindmapDir!))
        {
            var f = new Microsoft.Win32.OpenFolderDialog { Title = "选择保存目录（首次使用）" };
            if (f.ShowDialog(this) != true) return;
            cfg.MindmapDir = f.FolderName; ConfigManager.Save(cfg);
        }
        var d = new Microsoft.Win32.SaveFileDialog { Filter = _filter, Title = "保存", InitialDirectory = cfg.MindmapDir };
        if (d.ShowDialog(this) != true) return;
        var file = PathIO.GetExtension(d.FileName).ToLowerInvariant() == ".llk" ? d.FileName : d.FileName + ".llk";
        _doc.Save(file); _path = file; _dirty = false; AddRecent(file); UpdateTitle();
    }
    private void EnsureSaved(Action c) { if (!_dirty) { c(); return; } var r = MessageBox.Show(this, "有未保存的修改，是否保存？", "连连看", MessageBoxButton.YesNoCancel, MessageBoxImage.Question); if (r == MessageBoxResult.Yes) { Save(); c(); } else if (r == MessageBoxResult.No) c(); }
    private void UpdateTitle() { DocNameText.Text = _path == null ? "未命名" : PathIO.GetFileName(_path); Title = "连连看 — " + DocNameText.Text; }
    private void AddRecent(string file) { try { var c = ConfigManager.Load(); c.RecentMindmaps.Remove(file); c.RecentMindmaps.Insert(0, file); if (c.RecentMindmaps.Count > 20) c.RecentMindmaps.RemoveRange(20, c.RecentMindmaps.Count - 20); ConfigManager.Save(c); } catch { } }

    // ==================== 渲染 ====================
    private void Rebuild()
    {
        NodeCanvas.Children.Clear(); LinkCanvas.Children.Clear(); OverlayCanvas.Children.Clear();
        _nodeCards.Clear(); _portDots.Clear(); _portOwners.Clear();
        foreach (var n in _doc.Nodes) AddNodeCard(n);
        RefreshProps();
        RedrawLinks();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(RedrawLinks));
    }

    private void AddNodeCard(WfNode n)
    {
        var baseC = ColorFromHex(n.Color);
        var nodeBg = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(0xFA, baseC.R, baseC.G, baseC.B), 0),
            new GradientStop(Color.FromArgb(0xF2, (byte)(baseC.R * 0.82), (byte)(baseC.G * 0.82), (byte)(baseC.B * 0.82)), 1)
        }, new Point(0, 0), new Point(0, 1));

        var content = new StackPanel();
        var titleHost = new Border { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(20, 10, 20, 6) };
        var title = new TextBlock { Text = n.Title, FontSize = n.FontSize, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE5)), TextWrapping = TextWrapping.Wrap, MaxWidth = 240, TextAlignment = TextAlignment.Center };
        title.MouseLeftButtonDown += (s, e) => { if (IsDoubleClick()) { EditNodeTitle(n); e.Handled = true; } };
        titleHost.Child = title;
        content.Children.Add(titleHost);

        var portArea = new StackPanel { Margin = new Thickness(6, 2, 6, 8) };
        var rows = Math.Max(Math.Max(n.Inputs.Count, n.Outputs.Count), 1);
        for (int i = 0; i < rows; i++)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (i < n.Inputs.Count)
            {
                var p = n.Inputs[i];
                var dot = MakeDot(p, true);
                dot.HorizontalAlignment = HorizontalAlignment.Left;
                var label = PortLabel(p);
                label.HorizontalAlignment = HorizontalAlignment.Left; label.Margin = new Thickness(18, 0, 0, 0);
                row.Children.Add(dot); row.Children.Add(label);
                RegisterPort(p.Id, dot);
            }
            if (i < n.Outputs.Count)
            {
                var p = n.Outputs[i];
                var dot = MakeDot(p, false);
                dot.HorizontalAlignment = HorizontalAlignment.Right;
                var label = PortLabel(p);
                label.HorizontalAlignment = HorizontalAlignment.Right; label.Margin = new Thickness(0, 0, 18, 0);
                row.Children.Add(dot); row.Children.Add(label);
                RegisterPort(p.Id, dot);
            }
            row.Height = 24;
            portArea.Children.Add(row);
        }
        content.Children.Add(portArea);

        var card = new Border
        {
            Tag = n, Background = nodeBg, CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, baseC.R, baseC.G, baseC.B)), BorderThickness = new Thickness(1.4),
            Padding = new Thickness(0), MinWidth = 170, Child = content
        };
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, Opacity = 0.3, BlurRadius = 12, ShadowDepth = 1.5, Direction = 270 };
        Canvas.SetLeft(card, n.X); Canvas.SetTop(card, n.Y); Canvas.SetZIndex(card, 10);
        card.Measure(new Size(340, 240)); n.W = Math.Max(170, card.DesiredSize.Width + 8); card.Width = n.W;
        card.MouseLeftButtonDown += (s, e) => { SelectNode(n.Id); DragCardStart(card, e); };
        card.MouseMove += (s, e) => DragCardMove(card, e);
        card.MouseLeftButtonUp += (s, e) => DragCardEnd(card, e);
        card.ContextMenu = BuildNodeMenu(n);
        _nodeCards[n.Id] = card;
        NodeCanvas.Children.Add(card);
        foreach (var k in _portOwners.Keys.ToList()) if (_portOwners[k] == null!) _portOwners[k] = card;
    }

    private Ellipse MakeDot(Port p, bool isInput)
    {
        var c = ColorFromPortType(p.Type);
        return new Ellipse { Width = 11, Height = 11, Fill = isInput ? Brushes.Transparent : new SolidColorBrush(c), Stroke = new SolidColorBrush(c), StrokeThickness = 1.8, Cursor = Cursors.Cross, VerticalAlignment = VerticalAlignment.Center };
    }
    private static Color ColorFromPortType(string t) => t switch
    {
        PortType.Text => Color.FromRgb(0x96, 0xD0, 0xFF),
        PortType.Number => Color.FromRgb(0x8D, 0xDB, 0x8C),
        PortType.Image => Color.FromRgb(0xF6, 0x9D, 0x50),
        _ => Color.FromRgb(0xB4, 0xF1, 0xB4),
    };
    private TextBlock PortLabel(Port p) => new TextBlock { Text = string.IsNullOrEmpty(p.Name) ? (p.IsInput ? "入" : "出") : p.Name, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC8)), VerticalAlignment = VerticalAlignment.Center };

    private void RegisterPort(string pid, Ellipse dot)
    {
        _portDots[pid] = dot;
        dot.MouseLeftButtonDown += (s, e) =>
        {
            _dragPort = dot; _linkCur = GetPortCenter(dot); _linking = true; CanvasHost.CaptureMouse();
            RedrawLinkPreview(); e.Handled = true;
        };
    }
    private Point GetPortCenter(Ellipse dot) => dot.TranslatePoint(new Point(dot.Width / 2, dot.Height / 2), NodeCanvas);

    private void RedrawLinks()
    {
        LinkCanvas.Children.Clear(); OverlayCanvas.Children.Clear();
        foreach (var l in _doc.Edges)
        {
            if (!_portDots.TryGetValue(l.FromPort, out var f) || !_portDots.TryGetValue(l.ToPort, out var t)) continue;
            var p1 = GetPortCenter(f); var p2 = GetPortCenter(t);
            var mx = (p1.X + p2.X) / 2;
            var g = new StreamGeometry();
            using (var ctx = g.Open()) { ctx.BeginFigure(p1, false, false); ctx.BezierTo(new Point(mx, p1.Y), new Point(mx, p2.Y), p2, true, false); }
            g.Freeze();
            var stroke = (_selLinkId == l.Id) ? new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x9A, 0xA2, 0xB0, 0xB8));
            var ph = new System.Windows.Shapes.Path { Stroke = stroke, StrokeThickness = l.W, Data = g, Tag = l.Id, Cursor = Cursors.Hand };
            ph.MouseLeftButtonDown += (s, e) => { _selLinkId = l.Id; _selNodeId = null; RefreshProps(); RedrawLinks(); e.Handled = true; };
            LinkCanvas.Children.Add(ph);
            var dir = p2 - new Point(mx, p2.Y); if (dir.Length < 1e-4) dir = p2 - p1; dir.Normalize();
            var angle = Math.Atan2(dir.Y, dir.X) * 180 / Math.PI;
            double asz = 6 + l.W * 3.2;
            var ag = new StreamGeometry();
            using (var actx = ag.Open()) { actx.BeginFigure(new Point(0, 0), true, true); actx.LineTo(new Point(-asz, -asz * 0.5), true, false); actx.LineTo(new Point(-asz, asz * 0.5), true, false); }
            ag.Freeze();
            var arrow = new System.Windows.Shapes.Path { Fill = stroke, Data = ag, RenderTransform = new RotateTransform(angle) };
            Canvas.SetLeft(arrow, p2.X); Canvas.SetTop(arrow, p2.Y);
            OverlayCanvas.Children.Add(arrow);
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
        if (e.ChangedButton == MouseButton.Middle) { _panning = true; _panStart = e.GetPosition(this); CanvasHost.CaptureMouse(); e.Handled = true; return; }
        if (e.ChangedButton != MouseButton.Left) return;
        SelectNode(null); SelectLink(null);
        var now = DateTime.UtcNow;
        if ((now - _lastBlankDown).TotalMilliseconds < 350 && (e.GetPosition(this) - _lastBlankPt).Length < 8)
        {
            _lastBlankDown = DateTime.MinValue;
            var cv = e.GetPosition(NodeCanvas);
            var nn = new WfNode { Title = "节点", Kind = NodeKind.Process, X = cv.X - 85, Y = cv.Y - 30, Color = "#FF3A3A3C" };
            nn.Inputs.Add(new Port { Name = "入", Type = PortType.Any, IsInput = true });
            nn.Outputs.Add(new Port { Name = "出", Type = PortType.Any });
            BeforeChange(); _doc.Nodes.Add(nn); Rebuild(); _dirty = true; SelectNode(nn.Id);
            e.Handled = true; return;
        }
        _lastBlankDown = now; _lastBlankPt = e.GetPosition(this);
        _panning = true; _panStart = e.GetPosition(this); CanvasHost.CaptureMouse(); e.Handled = true;
    }
    private void OnCanvasMouseMove(object s, MouseEventArgs e)
    {
        if (_linking && _dragPort != null) { _linkCur = e.GetPosition(NodeCanvas); RedrawLinkPreview(); }
        else if (_panning) { var h = e.GetPosition(this); PanTf.X += (h.X - _panStart.X) / _zoom; PanTf.Y += (h.Y - _panStart.Y) / _zoom; _panStart = h; }
    }
    private void OnCanvasMouseUp(object s, MouseButtonEventArgs e)
    {
        if (_linking && _dragPort != null)
        {
            var drop = HitTestPort(e.GetPosition(NodeCanvas));
            if (drop != null && !ReferenceEquals(drop, _dragPort))
            {
                var fromPort = PortIdOf(_dragPort); var toPort = PortIdOf(drop);
                var fromP = FindPort(fromPort); var toP = FindPort(toPort);
                if (fromP != null && toP != null && fromP.IsInput != toP.IsInput && PortType.Compatible(fromP.Type, toP.Type))
                {
                    var (fp, tp) = fromP.IsInput ? (toPort, fromPort) : (fromPort, toPort);
                    if (!_doc.Edges.Any(x => x.FromPort == fp && x.ToPort == tp))
                    { BeforeChange(); _doc.Edges.Add(new WfEdge { FromPort = fp, ToPort = tp }); _dirty = true; }
                }
            }
        }
        _linking = false; _dragPort = null; _panning = false; CanvasHost.ReleaseMouseCapture(); RedrawLinks();
    }
    private void OnCanvasMouseLeave(object s, MouseEventArgs e) { if (_linking) { _linking = false; _dragPort = null; RedrawLinks(); } }

    private Port? FindPort(string id) { foreach (var n in _doc.Nodes) { var p = n.Inputs.FirstOrDefault(x => x.Id == id) ?? n.Outputs.FirstOrDefault(x => x.Id == id); if (p != null) return p; } return null; }
    private string PortIdOf(Ellipse dot) => _portDots.FirstOrDefault(x => ReferenceEquals(x.Value, dot)).Key ?? "";
    private Ellipse? HitTestPort(Point c)
    {
        Ellipse? hit = null; double best = 24;
        foreach (var kv in _portDots) { var d = (GetPortCenter(kv.Value) - c).Length; if (d < best) { best = d; hit = kv.Value; } }
        return hit;
    }

    // ==================== 节点拖拽 ====================
    private void DragCardStart(Border card, MouseButtonEventArgs e)
    {
        _dragCard = card; card.CaptureMouse();
        var c = e.GetPosition(NodeCanvas);
        _grabOffX = c.X - Canvas.GetLeft(card); _grabOffY = c.Y - Canvas.GetTop(card);
        e.Handled = true;
    }
    private void DragCardMove(Border card, MouseEventArgs e)
    {
        if (_dragCard == card && e.LeftButton == MouseButtonState.Pressed)
        {
            var c = e.GetPosition(NodeCanvas);
            Canvas.SetLeft(card, c.X - _grabOffX); Canvas.SetTop(card, c.Y - _grabOffY);
            var n = (WfNode)card.Tag; n.X = Canvas.GetLeft(card); n.Y = Canvas.GetTop(card);
            RedrawLinks(); _dirty = true;
        }
    }
    private void DragCardEnd(Border card, MouseButtonEventArgs e) { if (_dragCard == card) _dragCard = null; card.ReleaseMouseCapture(); }

    // ==================== 选择 / 属性 ====================
    private void SelectNode(string? id)
    {
        _selNodeId = id; _selLinkId = null;
        foreach (var kv in _nodeCards)
        {
            var node = (WfNode)kv.Value.Tag;
            kv.Value.BorderBrush = kv.Key == id
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Color.FromArgb(0x99, ColorFromHex(node.Color).R, ColorFromHex(node.Color).G, ColorFromHex(node.Color).B));
        }
        RefreshProps(); RedrawLinks();
    }
    private void SelectLink(string? id) { _selLinkId = id; _selNodeId = null; RefreshProps(); RedrawLinks(); }
    private void RefreshProps()
    {
        _suppressProp = true;
        try
        {
            PropParamPanel.Children.Clear();
            if (_selNodeId != null)
            {
                var n = _doc.Nodes.FirstOrDefault(x => x.Id == _selNodeId);
                if (n != null) { PropTitle.Text = "节点属性"; PropFontSize.Value = n.FontSize; BuildParamControls(n); }
            }
            else if (_selLinkId != null) { PropTitle.Text = "连线属性"; var l = _doc.Edges.FirstOrDefault(x => x.Id == _selLinkId); PropThickness.Value = l != null ? l.W : 4; }
            else { PropTitle.Text = "属性"; }
        }
        finally { _suppressProp = false; }
    }

    private void BuildParamControls(WfNode n)
    {
        foreach (var kv in n.Params.ToList())
        {
            PropParamPanel.Children.Add(new TextBlock { Text = LabelOf(n, kv.Key), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)), Margin = new Thickness(0, 8, 0, 4) });
            PropParamPanel.Children.Add(ControlOf(n, kv));
        }
    }
    private static string LabelOf(WfNode n, string key) { var def = NodeLibrary.FirstOrDefault(x => x.Title == n.Title); if (def != null) { var p = def.Params.FirstOrDefault(x => x.Key == key); if (p.Label != null) return p.Label; } return key; }
    private Control ControlOf(WfNode n, KeyValuePair<string, string> kv)
    {
        var box = new TextBox { Text = kv.Value, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)), Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), Padding = new Thickness(6, 4, 6, 4) };
        box.TextChanged += (s, e) => { if (_suppressProp || _selNodeId == null) return; n.Params[kv.Key] = box.Text; _dirty = true; };
        return box;
    }

    private void OnPickColor(object s, RoutedEventArgs e)
    {
        if (_selNodeId == null) return;
        var n = _doc.Nodes.FirstOrDefault(x => x.Id == _selNodeId); if (n == null) return;
        BeforeChange(); n.Color = (string)((Button)s).Tag; ApplyAccentColor(n); _dirty = true;
    }
    private void ApplyAccentColor(WfNode n)
    {
        if (_nodeCards.TryGetValue(n.Id, out var card))
        {
            var c = ColorFromHex(n.Color);
            card.Background = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0xFA, c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb(0xF2, (byte)(c.R * 0.82), (byte)(c.G * 0.82), (byte)(c.B * 0.82)), 1)
            }, new Point(0, 0), new Point(0, 1));
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, c.R, c.G, c.B));
        }
    }
    private void OnFontSizeChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressProp) return;
        BeforeChange();
        foreach (var n in _doc.Nodes) n.FontSize = e.NewValue;
        foreach (var card in _nodeCards.Values) if (card.Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is Border h && h.Child is TextBlock tb) tb.FontSize = e.NewValue;
        _dirty = true;
    }
    private void OnThicknessChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressProp) return;
        BeforeChange();
        foreach (var l in _doc.Edges) l.W = e.NewValue;
        RedrawLinks(); _dirty = true;
    }

    // ==================== 编辑 / 菜单 / 删除 ====================
    private bool IsDoubleClick() { var now = DateTime.UtcNow; var r = (now - _lastClick).TotalMilliseconds < 350; _lastClick = now; return r; }
    private void EditNodeTitle(WfNode n)
    {
        if (!_nodeCards.TryGetValue(n.Id, out var card)) return;
        if (card.Child is not StackPanel sp || sp.Children.Count == 0 || sp.Children[0] is not Border host) return;
        var box = new TextBox { Text = n.Title, FontSize = n.FontSize, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), BorderThickness = new Thickness(0), Padding = new Thickness(8, 4, 8, 4), TextAlignment = TextAlignment.Center };
        host.Child = box; box.Focus(); box.SelectAll();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { BeforeChange(); n.Title = box.Text.Trim(); if (n.Title.Length == 0) n.Title = "节点"; ReplaceTitle(host, n); _dirty = true; e.Handled = true; } else if (e.Key == Key.Escape) { ReplaceTitle(host, n); e.Handled = true; } };
        box.LostKeyboardFocus += (_, _) => { if (box.Text != n.Title && !string.IsNullOrWhiteSpace(box.Text)) { BeforeChange(); n.Title = box.Text.Trim(); _dirty = true; } ReplaceTitle(host, n); };
    }
    private static void ReplaceTitle(Border host, WfNode n)
    {
        var tb = new TextBlock { Text = n.Title, FontSize = n.FontSize, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE5)), TextWrapping = TextWrapping.Wrap, MaxWidth = 240, TextAlignment = TextAlignment.Center };
        host.Child = tb;
    }
    private ContextMenu BuildNodeMenu(WfNode n)
    {
        var m = new ContextMenu();
        var edit = new MenuItem { Header = "编辑标题" }; edit.Click += (_, _) => EditNodeTitle(n);
        var del = new MenuItem { Header = "删除节点" }; del.Click += (_, _) => DeleteNode(n.Id);
        m.Items.Add(edit); m.Items.Add(new Separator()); m.Items.Add(del);
        return m;
    }
    private void DeleteNode(string id)
    {
        var n = _doc.Nodes.FirstOrDefault(x => x.Id == id); if (n == null) return;
        BeforeChange();
        var ps = n.Inputs.Select(p => p.Id).Concat(n.Outputs.Select(p => p.Id)).ToHashSet();
        _doc.Nodes.Remove(n); _doc.Edges.RemoveAll(x => ps.Contains(x.FromPort) || ps.Contains(x.ToPort));
        _selNodeId = null; Rebuild(); _dirty = true;
    }
    private void DeleteLink(string id) { BeforeChange(); _doc.Edges.RemoveAll(x => x.Id == id); _selLinkId = null; RedrawLinks(); _dirty = true; }

    // ==================== 撤销 / 快捷键 ====================
    private void BeforeChange()
    {
        _undo.Push(CloneDoc(_doc));
        if (_undo.Count > 60) { var a = _undo.ToArray(); Array.Reverse(a); _undo.Clear(); foreach (var x in a.Take(59)) _undo.Push(x); }
        _redo.Clear();
    }
    private static WorkflowDoc CloneDoc(WorkflowDoc d) => new WorkflowDoc
    {
        Nodes = d.Nodes.Select(x => new WfNode { Id = x.Id, Title = x.Title, Kind = x.Kind, X = x.X, Y = x.Y, Color = x.Color, FontSize = x.FontSize, W = x.W, Status = x.Status, Inputs = x.Inputs.Select(p => new Port { Id = p.Id, Name = p.Name, Type = p.Type, IsInput = p.IsInput }).ToList(), Outputs = x.Outputs.Select(p => new Port { Id = p.Id, Name = p.Name, Type = p.Type, IsInput = p.IsInput }).ToList(), Params = new Dictionary<string, string>(x.Params) }).ToList(),
        Edges = d.Edges.Select(x => new WfEdge { Id = x.Id, FromPort = x.FromPort, ToPort = x.ToPort, W = x.W }).ToList()
    };
    private void Undo() { if (_undo.Count == 0) return; _redo.Push(CloneDoc(_doc)); _doc = _undo.Pop(); _dirty = true; Rebuild(); }
    private void Redo() { if (_redo.Count == 0) return; _undo.Push(CloneDoc(_doc)); _doc = _redo.Pop(); _dirty = true; Rebuild(); }
    private void OnUndo(object s, RoutedEventArgs e) => Undo();
    private void OnRedo(object s, RoutedEventArgs e) => Redo();

    private void OnWindowKeyDown(object s, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Z) { Undo(); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Y) { Redo(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { _linking = false; _dragPort = null; SelectNode(null); SelectLink(null); e.Handled = true; return; }
        if (e.Key == Key.N) { OnToolAdd(s, new RoutedEventArgs()); e.Handled = true; }
        if (e.Key == Key.Delete) { if (_selNodeId != null) DeleteNode(_selNodeId); if (_selLinkId != null) DeleteLink(_selLinkId); e.Handled = true; }
    }

    // ==================== 执行引擎（模拟） ====================
    private void OnRun(object s, RoutedEventArgs e) => RunGraph();
    private void OnStop(object s, RoutedEventArgs e) { _execCts?.Cancel(); _running = false; }
    private async void RunGraph()
    {
        if (_running) return;
        _running = true; _execCts = new CancellationTokenSource();
        try
        {
            var order = TopoSort();
            foreach (var id in order)
            {
                if (_execCts!.IsCancellationRequested) break;
                var n = _doc.Nodes.First(x => x.Id == id);
                n.Status = ExecStatus.Running; UpdateNodeStatusVisual(n);
                await System.Threading.Tasks.Task.Delay(400, _execCts.Token);
                n.Status = ExecStatus.Success; UpdateNodeStatusVisual(n);
            }
        }
        catch (OperationCanceledException) { foreach (var n in _doc.Nodes) if (n.Status == ExecStatus.Running) { n.Status = ExecStatus.Idle; UpdateNodeStatusVisual(n); } }
        finally { _running = false; }
    }

    private List<string> TopoSort()
    {
        var indegree = new Dictionary<string, int>();
        var adj = new Dictionary<string, List<string>>();
        foreach (var n in _doc.Nodes) { indegree[n.Id] = 0; adj[n.Id] = new List<string>(); }
        foreach (var e in _doc.Edges)
        {
            var src = NodeOfPort(e.FromPort); var dst = NodeOfPort(e.ToPort);
            if (src != null && dst != null && src != dst && !adj[src].Contains(dst))
            { adj[src].Add(dst); indegree[dst]++; }
        }
        var q = new Queue<string>(_doc.Nodes.Where(n => indegree[n.Id] == 0).Select(n => n.Id));
        var res = new List<string>();
        while (q.Count > 0) { var id = q.Dequeue(); res.Add(id); foreach (var nb in adj[id]) { indegree[nb]--; if (indegree[nb] == 0) q.Enqueue(nb); } }
        foreach (var n in _doc.Nodes) if (!res.Contains(n.Id)) res.Add(n.Id);
        return res;
    }
    private string? NodeOfPort(string portId)
    {
        foreach (var n in _doc.Nodes) if (n.Inputs.Any(p => p.Id == portId) || n.Outputs.Any(p => p.Id == portId)) return n.Id;
        return null;
    }
    private void UpdateNodeStatusVisual(WfNode n)
    {
        if (!_nodeCards.TryGetValue(n.Id, out var card)) return;
        card.BorderBrush = n.Status switch
        {
            ExecStatus.Running => new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x50)),
            ExecStatus.Success => new SolidColorBrush(Color.FromRgb(0x82, 0xDC, 0x96)),
            ExecStatus.Failed => new SolidColorBrush(Color.FromRgb(0xFF, 0x6E, 0x6E)),
            _ => new SolidColorBrush(Color.FromArgb(0x99, ColorFromHex(n.Color).R, ColorFromHex(n.Color).G, ColorFromHex(n.Color).B)),
        };
    }

    // ==================== 导出 ====================
    private void OnExport(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图片 (*.png)|*.png", Title = "导出图片" };
        if (d.ShowDialog(this) != true) return;
        try { var rt = new RenderTargetBitmap((int)CanvasHost.ActualWidth, (int)CanvasHost.ActualHeight, 96, 96, PixelFormats.Pbgra32); rt.Render(CanvasHost); var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rt)); using var fs = File.Create(d.FileName); enc.Save(fs); MessageBox.Show(this, "已导出：" + PathIO.GetFileName(d.FileName), "导出"); }
        catch (Exception ex) { MessageBox.Show(this, "导出失败：" + ex.Message, "错误"); }
    }

    // ==================== 窗口拖拽/resize ====================
    private bool _winDrag; private Point _winDragStart;
    private void OnTitlebarDown(object s, MouseButtonEventArgs e) { if (e.ChangedButton != MouseButton.Left) return; _winDrag = true; _winDragStart = e.GetPosition(this); ((Border)s).CaptureMouse(); e.Handled = true; }
    private void OnTitlebarMove(object s, MouseEventArgs e) { if (!_winDrag || e.LeftButton != MouseButtonState.Pressed) return; var cur = e.GetPosition(this); var d = cur - _winDragStart; var wa = SystemParameters.WorkArea; Left = Math.Clamp(Left + d.X, wa.Left, wa.Right - Width); Top = Math.Clamp(Top + d.Y, wa.Top, wa.Bottom - Height); e.Handled = true; }
    private void OnTitlebarUp(object s, MouseButtonEventArgs e) { _winDrag = false; ((Border)s).ReleaseMouseCapture(); e.Handled = true; }
    private bool _resizing; private Vector _rs; private double _rw, _rh;
    private void OnResizeGripDown(object s, MouseButtonEventArgs e) { _resizing = true; _rs = (Vector)e.GetPosition(this); _rw = Width; _rh = Height; ResizeGrip.CaptureMouse(); e.Handled = true; }
    private void OnWindowResizeMove(object s, MouseEventArgs e) { if (!_resizing || e.LeftButton != MouseButtonState.Pressed) return; var d = (Vector)e.GetPosition(this) - _rs; var wa = SystemParameters.WorkArea; Width = Math.Clamp(_rw + d.X, 960, wa.Width); Height = Math.Clamp(_rh + d.Y, 560, wa.Height); e.Handled = true; }
    private void OnResizeGripUp(object s, MouseButtonEventArgs e) { _resizing = false; ResizeGrip.ReleaseMouseCapture(); e.Handled = true; }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_dirty) { var r = MessageBox.Show(this, "有未保存的修改，是否保存？", "连连看", MessageBoxButton.YesNoCancel, MessageBoxImage.Question); if (r == MessageBoxResult.Yes) Save(); else if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; } }
        base.OnClosing(e);
    }
}