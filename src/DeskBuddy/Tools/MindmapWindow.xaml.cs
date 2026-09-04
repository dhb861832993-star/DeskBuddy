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

/// <summary>连连看：Xmind 风格自由画布思维导图（左工具条 + 顶栏 + 右属性面板）。</summary>
public partial class MindmapWindow : Window
{
    private MindmapDoc _doc = new();
    private string? _path;
    private bool _dirty;
    private double _zoom = 1.0;
    private readonly Dictionary<string, Border> _nodeVisuals = new();
    private readonly Dictionary<string, Ellipse> _connectors = new();
    private readonly Dictionary<string, System.Windows.Shapes.Path> _linkVisuals = new();

    // 撤销
    private readonly Stack<MindmapDoc> _undo = new();
    private readonly Stack<MindmapDoc> _redo = new();

    // 交互
    private bool _panning; private Point _panStart;
    private Border? _dragNode;
    private Point _dragStart; private bool _moved;
    private bool _linking; private Border? _linkFrom; private Point _linkCur;
    private string _selectNodeId = ""; private string _selectLinkId = "";
    private bool _suppressProp;

    private static readonly string _filter = "连连看图 (*.llk)|*.llk|JSON (*.json)|*.json|所有文件 (*.*)|*.*";

    public MindmapWindow()
    {
        InitializeComponent();
        ApplyTheme();
        InitSwatches();
        ToolSelect.Checked += (_, _) => { };
        NewDocument();
        CanvasHost.Loaded += (_, _) => CenterView();
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private void ApplyTheme()
    {
        var theme = Theme.From(Services.ConfigManager.Load().Theme);
        Resources["TextPrimary"] = Frozen(theme.TextPrimary);
        Resources["TextSecondary"] = Frozen(theme.TextSecondary);
        Resources["CardBorder"] = Frozen(theme.BorderColor);
        Resources["BtnBg"] = Frozen(theme.HoverBg);
        Resources["BtnBgHover"] = Frozen(Color.FromArgb(0x32, theme.HoverBg.R, theme.HoverBg.G, theme.HoverBg.B));
        Resources["CardBg"] = new SolidColorBrush(theme.CardTint) { Opacity = theme.CardAlpha };
        int delta = theme.IsDark ? -14 : 14; var cv = theme.CardTint;
        Resources["CanvasBg"] = new SolidColorBrush(Color.FromRgb(ClampByte(cv.R + delta), ClampByte(cv.G + delta), ClampByte(cv.B + delta)));
    }
    private static byte ClampByte(int v) => (byte)Math.Clamp(v, 0, 255);
    private void InitSwatches() { var sw = new[] { Swatch1, Swatch2, Swatch3, Swatch4, Swatch5 }; foreach (var s in sw) if (s != null) { try { s.Background = new SolidColorBrush(ColorFromHex((string)s.Tag)); } catch { } } }
    private Color ColorFromHex(string hex) { try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return Color.FromArgb(0x22, 0x4A, 0x90, 0xFF); } }

    // ==================== 变换核心 ====================
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

    // ==================== 工具模式 ====================
    private bool IsSelectTool => ToolSelect.IsChecked == true;
    private bool IsNodeTool => ToolNode.IsChecked == true;
    private bool IsLinkTool => ToolLink.IsChecked == true;
    private bool IsEraseTool => ToolErase.IsChecked == true;

    private void OnToolNode(object s, RoutedEventArgs e) { }

    // ==================== 画布事件 ====================
    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var host = e.GetPosition(CanvasHost); var canv = HostToCanvas(host);

        if (IsEraseTool) { var hit = HitTestNode(canv); if (hit != null) { DeleteNode(((MindNode)hit.Tag).Id); return; } }
        if (IsNodeTool) { var n = new MindNode { Text = "新节点", X = canv.X - 55, Y = canv.Y - 16 }; BeforeChange(); _doc.Nodes.Add(n); Rebuild(); _dirty = true; SelectNode(n.Id); EditNodeText(n); return; }

        // 双击空白新建
        _panning = true; _panStart = host; CanvasHost.CaptureMouse();
        SelectNode(""); SelectLink("");
        e.Handled = true;
    }
    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_linking && _linkFrom != null) { _linkCur = e.GetPosition(NodeCanvas); RedrawLinks(); RedrawLinkPreview(); }
        else if (_panning) { var h = e.GetPosition(CanvasHost); PanTf.X += h.X - _panStart.X; PanTf.Y += h.Y - _panStart.Y; _panStart = h; }
    }
    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_linking && _linkFrom != null)
        {
            var target = HitTestNode(e.GetPosition(NodeCanvas));
            if (target != null && !ReferenceEquals(target, _linkFrom) && !ExistsLink(target, _linkFrom))
            { BeforeChange(); _doc.Links.Add(new MindLink { From = NodeId(_linkFrom), To = NodeId(target) }); _dirty = true; }
        }
        _linking = false; _linkFrom = null; _panning = false; CanvasHost.ReleaseMouseCapture(); RedrawLinks();
    }
    private void OnCanvasMouseLeave(object s, MouseEventArgs e) { if (_linking) { _linking = false; _linkFrom = null; RedrawLinks(); } }

    // ==================== 节点 ====================
    private void OnNodeDown(object sender, MouseButtonEventArgs e)
    {
        var b = (Border)sender; _dragNode = b; _dragStart = e.GetPosition(NodeCanvas); _moved = false;
        SelectNode(((MindNode)b.Tag).Id);
        b.CaptureMouse(); e.Handled = true;
    }
    private void OnNodeMove(object sender, MouseEventArgs e)
    {
        if (_dragNode == sender && e.LeftButton == MouseButtonState.Pressed)
        {
            var c = e.GetPosition(NodeCanvas);
            if ((c - _dragStart).Length > 4) _moved = true;
            if (_moved) { MoveNode(_dragNode, c); }
        }
    }
    private void MoveNode(Border b, Point c)
    {
        var g = 10.0;
        var nx = Math.Round(c.X / g) * g; var ny = Math.Round(c.Y / g) * g;
        Canvas.SetLeft(b, nx - b.ActualWidth / 2); Canvas.SetTop(b, ny - b.ActualHeight / 2);
        var n = (MindNode)b.Tag; n.X = Canvas.GetLeft(b); n.Y = Canvas.GetTop(b);
        if (_connectors.TryGetValue(n.Id, out var cc)) { Canvas.SetLeft(cc, n.X + b.Width + 2); Canvas.SetTop(cc, n.Y + 18); }
        RedrawLinks(); _dirty = true;
    }
    private void OnNodeUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragNode == sender) _dragNode = null;
        ((Border)sender).ReleaseMouseCapture();
    }
    private void OnNodeDouble(MindNode n) => EditNodeText(n);

    // ==================== 连线（尾部小圆圈） ====================
    private void StartLinkFromCircle(Border node, Ellipse c, MouseButtonEventArgs e)
    {
        _linkFrom = node; _linkCur = new Point(Canvas.GetLeft(node) + node.Width + 9, Canvas.GetTop(node) + 25);
        _linking = true; CanvasHost.CaptureMouse(); RedrawLinks(); RedrawLinkPreview(); e.Handled = true;
    }
    private void OnLinkPick(object sender, MouseButtonEventArgs e)
    {
        var linkId = (string)((FrameworkElement)sender).Tag;
        SelectLink(linkId); SelectNode("");
        e.Handled = true;
    }

    // ==================== 渲染 ====================
    private void Rebuild()
    {
        NodeCanvas.Children.Clear(); LinkCanvas.Children.Clear();
        _nodeVisuals.Clear(); _connectors.Clear(); _linkVisuals.Clear();
        foreach (var n in _doc.Nodes) AddNodeVisual(n);
        RedrawLinks(); RefreshProps();
    }
    private void AddNodeVisual(MindNode n)
    {
        var border = new Border { Tag = n, Background = new SolidColorBrush(ColorFromHex(n.Color)), CornerRadius = new CornerRadius(10),
            BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(2), Padding = new Thickness(12, 8, 14, 8), Cursor = Cursors.Hand };
        var text = new TextBlock { Text = n.Text, FontSize = 12.5, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 230 };
        border.Child = text;
        border.Measure(new Size(300, 120));
        n.W = Math.Max(90, border.DesiredSize.Width + 8); border.Width = n.W;
        Canvas.SetLeft(border, n.X); Canvas.SetTop(border, n.Y);
        border.MouseLeftButtonDown += OnNodeDown; border.MouseMove += OnNodeMove; border.MouseLeftButtonUp += OnNodeUp;
        border.MouseRightButtonDown += (_, _) => { SelectNode(n.Id); EditNodeText(n); };
        var c = new Ellipse { Width = 14, Height = 14, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xFF)), StrokeThickness = 2, Cursor = Cursors.Cross, ToolTip = "拖到其它节点连线" };
        Canvas.SetLeft(c, n.X + n.W + 2); Canvas.SetTop(c, n.Y + 18); Canvas.SetZIndex(c, 40);
        c.MouseLeftButtonDown += (s2, e2) => StartLinkFromCircle(border, c, e2);
        _nodeVisuals[n.Id] = border; _connectors[n.Id] = c;
        NodeCanvas.Children.Add(border); NodeCanvas.Children.Add(c);
    }
    private void RedrawLinks()
    {
        OverlayCanvas.Children.Clear();
        LinkCanvas.Children.Clear(); _linkVisuals.Clear();
        foreach (var l in _doc.Links)
        {
            if (!_nodeVisuals.TryGetValue(l.From, out var a) || !_nodeVisuals.TryGetValue(l.To, out var b)) continue;
            var p1 = Center(a); var p2 = Center(b);
            var g = new StreamGeometry();
            using (var ctx = g.Open()) { ctx.BeginFigure(p1, false, false); var mx = p1.X + (p2.X - p1.X) / 2; ctx.BezierTo(new Point(mx, p1.Y), new Point(mx, p2.Y), p2, true, false); }
            g.Freeze();
            var ph = new System.Windows.Shapes.Path { Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), StrokeThickness = l.W >= 1 ? l.W : 1.6, Data = g, Tag = l.Id, Cursor = Cursors.Hand };
            ph.MouseLeftButtonDown += OnLinkPick;
            if (_selectLinkId == l.Id) { ph.Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xFF)); ph.StrokeThickness += 2; }
            _linkVisuals[l.Id] = ph;
            LinkCanvas.Children.Add(ph);
        }
    }
    private void RedrawLinkPreview()
    {
        OverlayCanvas.Children.Clear();
        if (_linkFrom == null) return;
        var p1 = Center(_linkFrom);
        var ph = new System.Windows.Shapes.Path { Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0x4A, 0x90, 0xFF)), StrokeThickness = 2.2, StrokeDashArray = new DoubleCollection { 4, 3 }, Data = new LineGeometry(p1, _linkCur) };
        OverlayCanvas.Children.Add(ph);
    }
    private Point Center(Border b) => new Point(Canvas.GetLeft(b) + b.ActualWidth / 2, Canvas.GetTop(b) + b.ActualHeight / 2);
    private string NodeId(Border b) => ((MindNode)b.Tag!).Id;
    private bool ExistsLink(Border a, Border b) => _doc.Links.Any(x => (x.From == NodeId(a) && x.To == NodeId(b)) || (x.From == NodeId(b) && x.To == NodeId(a)));
    private Border? HitTestNode(Point c)
    {
        foreach (var b in _nodeVisuals.Values) { var l = Canvas.GetLeft(b); var t = Canvas.GetTop(b); if (c.X >= l && c.X <= l + b.ActualWidth && c.Y >= t && c.Y <= t + b.ActualHeight) return b; }
        return null;
    }

    // ==================== 选择 / 属性 ====================
    private void SelectNode(string id)
    {
        _selectNodeId = id; _selectLinkId = "";
        foreach (var kv in _nodeVisuals) kv.Value.BorderBrush = kv.Key == id ? new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xFF)) : Brushes.Transparent;
        RefreshProps(); RedrawLinks();
    }
    private void SelectLink(string id)
    {
        _selectLinkId = id;
        RefreshProps(); RedrawLinks();
    }
    private void RefreshProps()
    {
        _suppressProp = true;
        try
        {
            if (_selectNodeId != "")
            {
                var n = _doc.Nodes.FirstOrDefault(x => x.Id == _selectNodeId);
                if (n != null) { PropTitle.Text = "节点属性"; PropText.Text = n.Text; PropThickness.Value = 1.6; PropArrow.IsEnabled = false; }
            }
            else if (_selectLinkId != "")
            {
                var l = _doc.Links.FirstOrDefault(x => x.Id == _selectLinkId);
                if (l != null) { PropTitle.Text = "连线属性"; PropText.Text = ""; PropThickness.Value = l.W >= 1 ? l.W : 1.6; PropArrow.IsEnabled = true; }
            }
            else { PropTitle.Text = "属性"; PropText.Text = ""; }
        }
        finally { _suppressProp = false; }
    }
    private void OnPropTextChanged(object s, TextChangedEventArgs e)
    {
        if (_suppressProp) return;
        if (_selectNodeId != "" && _nodeVisuals.TryGetValue(_selectNodeId, out var b))
        { var n = (MindNode)b.Tag; if (n.Text != PropText.Text) { n.Text = PropText.Text; var tb = (TextBlock)b.Child; tb.Text = n.Text; b.Measure(new Size(300, 120)); n.W = Math.Max(90, b.DesiredSize.Width + 8); b.Width = n.W; _dirty = true; } }
    }
    private void OnPickColor(object s, RoutedEventArgs e)
    {
        if (_selectNodeId == "") return;
        var col = (string)((Button)s).Tag; var n = _doc.Nodes.FirstOrDefault(x => x.Id == _selectNodeId); if (n == null) return;
        BeforeChange(); n.Color = col; if (_nodeVisuals.TryGetValue(n.Id, out var b)) b.Background = new SolidColorBrush(ColorFromHex(col)); _dirty = true;
    }
    private void OnCycleColor(object s, RoutedEventArgs e)
    {
        if (_selectNodeId == "") return;
        var n = _doc.Nodes.FirstOrDefault(x => x.Id == _selectNodeId); if (n == null) return;
        var cols = new[] { "#224A90FF", "#22FF453A", "#2232D583", "#22FF9F0A", "#22BF5AF2" };
        var i = Array.IndexOf(cols, n.Color); IfFoundColor(i, out int idx, cols);
        BeforeChange(); n.Color = cols[idx]; if (_nodeVisuals.TryGetValue(n.Id, out var b)) b.Background = new SolidColorBrush(ColorFromHex(n.Color)); _dirty = true;
    }
    private void IfFoundColor(int i, out int idx, string[] cols) { idx = (i + 1) % cols.Length; if (i < 0) idx = 0; }

    // ==================== 撤销 / 文档 / 导出 ====================
    private void BeforeChange()
    {
        _undo.Push(CloneDoc(_doc)); if (_undo.Count > 60) { var a = _undo.ToArray(); Array.Reverse(a); _undo.Clear(); foreach (var x in a.Take(59)) _undo.Push(x); }
        _redo.Clear();
    }
    private static MindmapDoc CloneDoc(MindmapDoc d) => new MindmapDoc { Nodes = d.Nodes.Select(x => new MindNode { Id = x.Id, Text = x.Text, X = x.X, Y = x.Y, Color = x.Color, W = x.W }).ToList(), Links = d.Links.Select(x => new MindLink { Id = x.Id, From = x.From, To = x.To, W = x.W }).ToList() };
    private void Undo() { if (_undo.Count == 0) return; _redo.Push(CloneDoc(_doc)); _doc = _undo.Pop(); _dirty = true; Rebuild(); }
    private void Redo() { if (_redo.Count == 0) return; _undo.Push(CloneDoc(_doc)); _doc = _redo.Pop(); _dirty = true; Rebuild(); }
    private void OnUndo(object s, RoutedEventArgs e) => Undo();
    private void OnRedo(object s, RoutedEventArgs e) => Redo();

    public void NewDocument() { BeforeChange(); _doc = new MindmapDoc(); _path = null; _dirty = false;
        _doc.Nodes.Add(new MindNode { Text = "中心主题", X = 0, Y = 0, W = 140 });
        _doc.Nodes.Add(new MindNode { Text = "分支 A", X = 280, Y = -120 }); _doc.Nodes.Add(new MindNode { Text = "分支 B", X = 280, Y = 120 });
        _doc.Links.Add(new MindLink { From = _doc.Nodes[0].Id, To = _doc.Nodes[1].Id }); _doc.Links.Add(new MindLink { From = _doc.Nodes[0].Id, To = _doc.Nodes[2].Id });
        Rebuild(); UpdateTitle(); }
    private void OnNew(object s, RoutedEventArgs e) => EnsureSaved(NewDocument);
    private void OnOpen(object s, RoutedEventArgs e) => EnsureSaved(() => { var d = new Microsoft.Win32.OpenFileDialog { Filter = _filter, Title = "打开" }; if (d.ShowDialog(this) == true) { _doc = MindmapDoc.Load(d.FileName); _path = d.FileName; _dirty = false; Rebuild(); UpdateTitle(); AddRecent(d.FileName); } });
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
    private void OnExport(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图片 (*.png)|*.png", Title = "导出图片" };
        if (d.ShowDialog(this) != true) return;
        try { var rt = new RenderTargetBitmap((int)CanvasHost.ActualWidth, (int)CanvasHost.ActualHeight, 96, 96, PixelFormats.Pbgra32); rt.Render(CanvasHost); var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rt)); using var fs = File.Create(d.FileName); enc.Save(fs); MessageBox.Show(this, "已导出：" + PathIO.GetFileName(d.FileName), "导出"); } catch (Exception ex) { MessageBox.Show(this, "导出失败：" + ex.Message, "错误"); }
    }
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Z) { Undo(); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Y) { Redo(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { _linking = false; _linkFrom = null; RedrawLinks(); SelectNode(""); SelectLink(""); e.Handled = true; return; }
        if (e.Key == Key.V) { ToolSelect.IsChecked = true; e.Handled = true; }
        if (e.Key == Key.N) { ToolNode.IsChecked = true; e.Handled = true; }
        if (e.Key == Key.L) { ToolLink.IsChecked = true; e.Handled = true; }
        if (e.Key == Key.E) { ToolErase.IsChecked = true; e.Handled = true; }
        if (e.Key == Key.Delete) { if (_selectNodeId != "") DeleteNode(_selectNodeId); if (_selectLinkId != "") DeleteLink(_selectLinkId); e.Handled = true; }
        if (e.Key == Key.F2) EditNodeText(_doc.Nodes.FirstOrDefault(x => x.Id == _selectNodeId));
    }
    private void DeleteNode(string id) { var n = _doc.Nodes.FirstOrDefault(x => x.Id == id); if (n == null) return; BeforeChange(); _doc.Nodes.Remove(n); _doc.Links.RemoveAll(x => x.From == id || x.To == id); _selectNodeId = ""; Rebuild(); _dirty = true; }
    private void DeleteLink(string id) { BeforeChange(); _doc.Links.RemoveAll(x => x.Id == id); _selectLinkId = ""; Rebuild(); _dirty = true; }
    private void EditNodeText(MindNode n)
    {
        if (n == null || !_nodeVisuals.TryGetValue(n.Id, out var b)) return;
        var box = new TextBox { Text = n.Text, FontSize = 12.5, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), BorderThickness = new Thickness(0), Padding = new Thickness(6, 4, 6, 4) };
        b.Child = box; box.Focus(); box.SelectAll();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { BeforeChange(); n.Text = box.Text.Trim(); if (n.Text.Length == 0) n.Text = "新节点"; Rebuild(); _dirty = true; e.Handled = true; }
            else if (e.Key == Key.Escape) { Rebuild(); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => { if (box.Text != n.Text && !string.IsNullOrWhiteSpace(box.Text)) { BeforeChange(); n.Text = box.Text.Trim(); _dirty = true; } Rebuild(); };
    }
    private void OnArrowChanged(object s, RoutedEventArgs e) { if (_suppressProp) return; }
    private void OnThicknessChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressProp || _selectLinkId == "") return; var l = _doc.Links.FirstOrDefault(x => x.Id == _selectLinkId); if (l == null) return; l.W = e.NewValue; RedrawLinks(); _dirty = true; }

    // ==================== 窗口 resize ====================
    private bool _resizing; private Vector _rs; private double _rw, _rh;
    private void OnResizeGripDown(object s, MouseButtonEventArgs e) { _resizing = true; _rs = (Vector)e.GetPosition(this); _rw = Width; _rh = Height; ResizeGrip.CaptureMouse(); e.Handled = true; }
    private void OnWindowResizeMove(object s, MouseEventArgs e) { if (!_resizing || e.LeftButton != MouseButtonState.Pressed) return; var d = (Vector)e.GetPosition(this) - _rs; var wa = SystemParameters.WorkArea; Width = Math.Clamp(_rw + d.X, 900, wa.Width); Height = Math.Clamp(_rh + d.Y, 540, wa.Height); e.Handled = true; }
    private void OnResizeGripUp(object s, MouseButtonEventArgs e) { _resizing = false; ResizeGrip.ReleaseMouseCapture(); e.Handled = true; }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_dirty) { var r = MessageBox.Show(this, "有未保存的修改，是否保存？", "连连看", MessageBoxButton.YesNoCancel, MessageBoxImage.Question); if (r == MessageBoxResult.Yes) Save(); else if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; } }
        base.OnClosing(e);
    }
}