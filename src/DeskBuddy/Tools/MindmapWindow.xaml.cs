using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DeskBuddy.Models;
using DeskBuddy.Services;
using PathIO = System.IO.Path;

namespace DeskBuddy.Tools;

/// <summary>连连看：自由画布思维导图。坐标核心——节点用「画布坐标」存储，
/// 缩放/平移统一在 CanvasGroup 上，命中/放置一律用 GetPosition(NodeCanvas) 得到画布坐标。</summary>
public partial class MindmapWindow : Window
{
    private MindmapDoc _doc = new();
    private string? _path;
    private readonly Dictionary<string, Border> _nodeVisuals = new();
    private bool _dirty;
    private double _zoom = 1.0;

    // 画布 Viewport 坐标（挂 CanvasHost 下，不含变换）
    private Point _panStartHost;
    private bool _panning;

    // 节点拖拽
    private Border? _dragNode;

    // 连线
    private Border? _linkFrom;
    private Point _linkCur;
    private bool _linking;

    // 空白双击检测
    private DateTime _lastBlankDown = DateTime.MinValue;
    private Point _lastBlankDownHost;

    // 节点双击检测
    private DateTime _lastNodeDown = DateTime.MinValue;
    private Border? _lastNodeDownBorder;

    private static readonly string _filter = "连连看图 (*.llk)|*.llk|JSON (*.json)|*.json|所有文件 (*.*)|*.*";

    public MindmapWindow()
    {
        InitializeComponent();
        ApplyTheme();
        NewDocument();
        // 初始平移：把(0,0)放到可见区域中心
        CanvasHost.Loaded += (_, _) => CenterView();
    }

    // ==================== 主题 ====================
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private void ApplyTheme()
    {
        var theme = Theme.From(Services.ConfigManager.Load().Theme);
        Resources["TextPrimary"] = Frozen(theme.TextPrimary);
        Resources["TextSecondary"] = Frozen(theme.TextSecondary);
        Resources["CardBorder"] = Frozen(theme.BorderColor);
        Resources["BtnBg"] = Frozen(theme.HoverBg);
        Resources["BtnBgHover"] = Frozen(Color.FromArgb(0x3D, theme.HoverBg.R, theme.HoverBg.G, theme.HoverBg.B));
        Resources["CardBg"] = new SolidColorBrush(theme.CardTint) { Opacity = theme.CardAlpha };
        int delta = theme.IsDark ? -16 : 14;
        var cv = theme.CardTint;
        Resources["CanvasBg"] = new SolidColorBrush(Color.FromRgb(ClampByte(cv.R + delta), ClampByte(cv.G + delta), ClampByte(cv.B + delta)));
    }
    private static byte ClampByte(int v) => (byte)Math.Clamp(v, 0, 255);

    // ==================== 画布变换（统一 CanvasGroup） ====================

    /// <summary>画布坐标某个点（当前可见视口坐标原点）。</summary>
    private Point HostToCanvas(Point hostPt) => new Point((hostPt.X - PanTf.X) / _zoom, (hostPt.Y - PanTf.Y) / _zoom);

    private void CenterView()
    {
        PanTf.X = CanvasHost.ActualWidth / 2;
        PanTf.Y = CanvasHost.ActualHeight / 2;
    }

    private void SetZoom(double newZoom, Point focusHost)
    {
        newZoom = Math.Clamp(newZoom, 0.25, 3.0);
        if (Math.Abs(newZoom - _zoom) < 0.0001) return;
        // 保持 focusHost(视口坐标) 下的画布点不移动
        var canvasPt = HostToCanvas(focusHost);
        _zoom = newZoom;
        ZoomTf.ScaleX = ZoomTf.ScaleY = _zoom;
        PanTf.X = focusHost.X - canvasPt.X * _zoom;
        PanTf.Y = focusHost.Y - canvasPt.Y * _zoom;
        ZoomText.Text = (int)(_zoom * 100) + "%";
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var focus = e.GetPosition(CanvasHost);
        SetZoom(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), focus);
        e.Handled = true;
    }
    private void OnZoomIn(object s, RoutedEventArgs e) => SetZoom(_zoom * 1.2, CenterHost());
    private void OnZoomOut(object s, RoutedEventArgs e) => SetZoom(_zoom / 1.2, CenterHost());
    private Point CenterHost() => new Point(CanvasHost.ActualWidth / 2, CanvasHost.ActualHeight / 2);

    // ==================== 画布鼠标：平移 / 空白双击 ====================

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var host = e.GetPosition(CanvasHost);

        // 空白双击 → 新建节点
        if ((DateTime.UtcNow - _lastBlankDown).TotalMilliseconds < 350 && (host - _lastBlankDownHost).Length < 6)
        {
            var c = HostToCanvas(host);
            var n = new MindNode { Text = "新节点", X = c.X - 55, Y = c.Y - 16 };
            _doc.Nodes.Add(n); Rebuild(); _dirty = true; EditNodeText(n);
            _lastBlankDown = DateTime.MinValue;
            e.Handled = true;
            return;
        }
        _lastBlankDown = DateTime.UtcNow; _lastBlankDownHost = host;

        _panning = true; _panStartHost = host;
        CanvasHost.CaptureMouse();
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_linking && _linkFrom != null)
        {
            _linkCur = e.GetPosition(NodeCanvas);
            RedrawLinkPreview();
        }
        else if (_panning)
        {
            var h = e.GetPosition(CanvasHost);
            PanTf.X += h.X - _panStartHost.X;
            PanTf.Y += h.Y - _panStartHost.Y;
            _panStartHost = h;
            // 连线/节点不需要重绘（都在同一 CanvasGroup，平移由变换带动）
        }
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_linking && _linkFrom != null)
        {
            var target = HitTestNode(e.GetPosition(NodeCanvas));
            if (target != null && !ReferenceEquals(target, _linkFrom) &&
                !_doc.Links.Any(x => x.From == NodeId(_linkFrom) && x.To == NodeId(target)))
            {
                _doc.Links.Add(new MindLink { From = NodeId(_linkFrom), To = NodeId(target) });
                _dirty = true;
            }
        }
        _linking = false; _linkFrom = null; _panning = false;
        CanvasHost.ReleaseMouseCapture();
        RedrawLinks();
    }

    private void OnCanvasMouseLeave(object sender, MouseEventArgs e)
    {
        if (_linking) { _linking = false; _linkFrom = null; RedrawLinks(); }
    }

    // ==================== 文档 ====================
    public void NewDocument()
    {
        _doc = new MindmapDoc(); _path = null; _dirty = false;
        _doc.Nodes.Add(new MindNode { Text = "中心主题", X = 0, Y = 0, W = 130 });
        _doc.Nodes.Add(new MindNode { Text = "分支 1", X = 260, Y = -110 });
        _doc.Nodes.Add(new MindNode { Text = "分支 2", X = 260, Y = 110 });
        _doc.Links.Add(new MindLink { From = _doc.Nodes[0].Id, To = _doc.Nodes[1].Id });
        _doc.Links.Add(new MindLink { From = _doc.Nodes[0].Id, To = _doc.Nodes[2].Id });
        Rebuild(); UpdateTitle();
    }
    public void LoadPath(string path) { _doc = MindmapDoc.Load(path); _path = path; _dirty = false; Rebuild(); UpdateTitle(); AddRecent(path); }

    private void OnNew(object s, RoutedEventArgs e) => EnsureSaved(NewDocument);
    private void OnOpen(object s, RoutedEventArgs e) => EnsureSaved(() =>
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = _filter, Title = "打开连连看" };
        if (dlg.ShowDialog(this) == true) LoadPath(dlg.FileName);
    });
    private void OnSave(object s, RoutedEventArgs e) => Save();
    private void OnHistory(object s, RoutedEventArgs e) => ShowHistory();
    private void OnClose(object s, RoutedEventArgs e) => EnsureSaved(Close);

    private void Save() { if (_path == null) ChooseDirAndSave(); else { _doc.Save(_path); _dirty = false; AddRecent(_path); UpdateTitle(); } }
    private void ChooseDirAndSave()
    {
        var cfg = ConfigManager.Load();
        if (string.IsNullOrWhiteSpace(cfg.MindmapDir) || !Directory.Exists(cfg.MindmapDir!))
        {
            var folder = new Microsoft.Win32.OpenFolderDialog { Title = "选择连连看保存目录（首次使用）" };
            if (folder.ShowDialog(this) != true) return;
            cfg.MindmapDir = folder.FolderName; ConfigManager.Save(cfg);
        }
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = _filter, Title = "保存连连看", InitialDirectory = cfg.MindmapDir };
        if (dlg.ShowDialog(this) != true) return;
        var file = PathIO.GetExtension(dlg.FileName).ToLowerInvariant() == ".llk" ? dlg.FileName : dlg.FileName + ".llk";
        _doc.Save(file); _path = file; _dirty = false; AddRecent(file); UpdateTitle();
    }
    private void EnsureSaved(Action cont)
    {
        if (!_dirty) { cont(); return; }
        var r = MessageBox.Show(this, "有未保存的修改，是否保存？", "连连看", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) { Save(); cont(); }
        else if (r == MessageBoxResult.No) cont();
    }
    private void UpdateTitle() { DocNameText.Text = _path == null ? "未命名" : PathIO.GetFileName(_path); Title = "🌳 连连看 — " + DocNameText.Text; }
    private void AddRecent(string file) { try { var cfg = ConfigManager.Load(); cfg.RecentMindmaps.Remove(file); cfg.RecentMindmaps.Insert(0, file); if (cfg.RecentMindmaps.Count > 20) cfg.RecentMindmaps.RemoveRange(20, cfg.RecentMindmaps.Count - 20); ConfigManager.Save(cfg); } catch { } }
    private void ShowHistory()
    {
        var cfg = ConfigManager.Load(); var list = cfg.RecentMindmaps.Where(File.Exists).ToList();
        if (list.Count == 0) { MessageBox.Show(this, "暂无历史记录。", "历史"); return; }
        var menu = new ContextMenu();
        foreach (var f in list.Take(12)) menu.Items.Add(new MenuItem { Header = PathIO.GetFileName(f), ToolTip = f, Tag = f });
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "清空历史" }; clear.Click += (_, _) => { cfg.RecentMindmaps.Clear(); ConfigManager.Save(cfg); }; menu.Items.Add(clear);
        foreach (var it in menu.Items.OfType<MenuItem>().Where(x => x.Tag is string)) it.Click += (s, _) => EnsureSaved(() => LoadPath((string)((MenuItem)s!).Tag!));
        menu.IsOpen = true;
    }

    // ==================== 渲染 ====================
    private void Rebuild()
    {
        NodeCanvas.Children.Clear(); _nodeVisuals.Clear();
        foreach (var n in _doc.Nodes) AddNodeVisual(n);
        RedrawLinks();
    }
    private void AddNodeVisual(MindNode n)
    {
        var border = new Border
        {
            Tag = n,
            Background = new SolidColorBrush(ColorFromHex(n.Color)),
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Cursor = Cursors.Hand
        };
        var text = new TextBlock { Text = n.Text, FontSize = 12.5, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 250 };
        border.Child = text;
        border.Measure(new Size(300, 80));
        n.W = Math.Max(90, border.DesiredSize.Width + 8);
        border.Width = n.W;
        Canvas.SetLeft(border, n.X); Canvas.SetTop(border, n.Y);

        border.MouseLeftButtonDown += OnNodeDown;
        border.MouseMove += OnNodeMove;
        border.MouseLeftButtonUp += OnNodeUp;
        border.MouseLeave += OnNodeMouseLeave;
        border.ContextMenu = BuildNodeMenu(n);
        _nodeVisuals[n.Id] = border;
        NodeCanvas.Children.Add(border);
    }

    private ContextMenu BuildNodeMenu(MindNode n)
    {
        var m = new ContextMenu();
        var edit = new MenuItem { Header = "编辑文字" }; edit.Click += (_, _) => EditNodeText(n);
        var link = new MenuItem { Header = "连线到…" }; link.Click += (_, _) => StartLink(n);
        var color = new MenuItem { Header = "更换颜色" }; color.Click += (_, _) => CycleNodeColor(n);
        var del = new MenuItem { Header = "删除节点" }; del.Click += (_, _) => DeleteNode(n);
        m.Items.Add(edit); m.Items.Add(link); m.Items.Add(color); m.Items.Add(new Separator()); m.Items.Add(del);
        return m;
    }

    private Color ColorFromHex(string hex) { try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return Color.FromArgb(0x22, 0x4A, 0x90, 0xFF); } }

    private void RedrawLinks()
    {
        LinkCanvas.Children.Clear();
        foreach (var l in _doc.Links)
        {
            if (!_nodeVisuals.TryGetValue(l.From, out var a) || !_nodeVisuals.TryGetValue(l.To, out var b)) continue;
            var p1 = Center(a); var p2 = Center(b);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(p1, false, false);
                var mx = p1.X + (p2.X - p1.X) / 2;
                ctx.BezierTo(new Point(mx, p1.Y), new Point(mx, p2.Y), p2, true, false);
            }
            g.Freeze();
            LinkCanvas.Children.Add(new System.Windows.Shapes.Path { Stroke = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1.6, Data = g });
        }
    }
    private Point Center(Border b) => new Point(Canvas.GetLeft(b) + b.ActualWidth / 2, Canvas.GetTop(b) + b.ActualHeight / 2);
    private string NodeId(Border b) => ((MindNode)b.Tag!).Id;

    // ==================== 节点交互 ====================

    private void OnNodeDown(object sender, MouseButtonEventArgs e)
    {
        // 双击检测（第二次按下间隔<350ms 同一节点）→ 编辑
        if (_lastNodeDownBorder == sender && (DateTime.UtcNow - _lastNodeDown).TotalMilliseconds < 350)
        {
            _dragNode = null; _lastNodeDown = DateTime.MinValue; _lastNodeDownBorder = null;
            EditNodeText((MindNode)((Border)sender).Tag);
            e.Handled = true; return;
        }
        _lastNodeDown = DateTime.UtcNow; _lastNodeDownBorder = sender as Border;

        var b = (Border)sender;
        var canv = e.GetPosition(NodeCanvas);

        // 在节点边缘（8px 内）按下 → 进入「拖线连线」模式
        var left = Canvas.GetLeft(b); var top = Canvas.GetTop(b);
        var insideEdge = canv.X < left + 8 || canv.X > left + b.ActualWidth - 8 ||
                         canv.Y < top + 8 || canv.Y > top + b.ActualHeight - 8;
        if (insideEdge)
        {
            _linkFrom = b; _linkCur = canv; _linking = true;
            CanvasHost.CaptureMouse();
            RedrawLinks(); RedrawLinkPreview();
            e.Handled = true; return;
        }

        // 中间 → 拖拽移动
        _dragNode = b;
        b.CaptureMouse();
        e.Handled = true;
    }

    private void OnNodeMove(object sender, MouseEventArgs e)
    {
        if (_dragNode == sender && e.LeftButton == MouseButtonState.Pressed)
        {
            var c = e.GetPosition(NodeCanvas);
            Canvas.SetLeft(_dragNode, c.X - _dragNode.ActualWidth / 2);
            Canvas.SetTop(_dragNode, c.Y - _dragNode.ActualHeight / 2);
            RedrawLinks(); _dirty = true;
        }
    }
    private void OnNodeUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragNode == sender) _dragNode = null;
        ((Border)sender).ReleaseMouseCapture();
    }
    private void OnNodeMouseLeave(object sender, MouseEventArgs e)
    {
        // 拖线过程中移出节点 → 交给画布继续跟踪（不自爆）
    }

    // ==================== 编辑 / 删除 / 颜色 / 连线 ====================

    private void EditNodeText(MindNode n)
    {
        if (!_nodeVisuals.TryGetValue(n.Id, out var b)) return;
        var box = new TextBox { Text = n.Text, FontSize = 12.5, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), BorderThickness = new Thickness(0), Padding = new Thickness(6, 4, 6, 4) };
        b.Child = box; box.Focus(); box.SelectAll();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { n.Text = box.Text.Trim(); if (n.Text.Length == 0) n.Text = "新节点"; Rebuild(); _dirty = true; e.Handled = true; }
            else if (e.Key == Key.Escape) { Rebuild(); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => { if (box.Text != n.Text && !string.IsNullOrWhiteSpace(box.Text)) { n.Text = box.Text.Trim(); _dirty = true; } Rebuild(); };
    }

    private void DeleteNode(MindNode n)
    {
        _doc.Nodes.RemoveAll(x => x.Id == n.Id);
        _doc.Links.RemoveAll(x => x.From == n.Id || x.To == n.Id);
        Rebuild(); _dirty = true;
    }
    private static readonly string[] _colors = { "#224A90FF", "#22FF453A", "#2232D583", "#22FF9F0A", "#22BF5AF2", "#22FF2D95" };
    private void CycleNodeColor(MindNode n)
    {
        var i = Array.IndexOf(_colors, n.Color); n.Color = _colors[(i + 1) % _colors.Length];
        if (_nodeVisuals.TryGetValue(n.Id, out var b)) b.Background = new SolidColorBrush(ColorFromHex(n.Color));
        _dirty = true;
    }

    private void StartLink(MindNode n)
    {
        if (!_nodeVisuals.TryGetValue(n.Id, out var b)) return;
        _linkFrom = b; _linkCur = Center(b); _linking = true; CanvasHost.CaptureMouse();
        RedrawLinks(); RedrawLinkPreview();
    }
    private void RedrawLinkPreview()
    {
        foreach (System.Windows.Shapes.Path p in LinkCanvas.Children.OfType<System.Windows.Shapes.Path>().Where(x => x.Tag?.ToString() == "preview").ToList()) LinkCanvas.Children.Remove(p);
        if (_linkFrom == null) return;
        var p1 = Center(_linkFrom);
        var line = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0x4A, 0x90, 0xFF)),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Tag = "preview",
            Data = new LineGeometry(p1, _linkCur)
        };
        Canvas.SetZIndex(line, 999); LinkCanvas.Children.Add(line);
    }

    private Border? HitTestNode(Point canvasPt)
    {
        foreach (var b in _nodeVisuals.Values)
        {
            var left = Canvas.GetLeft(b); var top = Canvas.GetTop(b);
            if (canvasPt.X >= left && canvasPt.X <= left + b.ActualWidth && canvasPt.Y >= top && canvasPt.Y <= top + b.ActualHeight) return b;
        }
        return null;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _linking) { _linking = false; _linkFrom = null; RedrawLinks(); e.Handled = true; }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_dirty)
        {
            var r = MessageBox.Show(this, "有未保存的修改，是否保存？", "连连看", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes) Save();
            else if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        }
        base.OnClosing(e);
    }
}