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

/// <summary>连连看：Xmind 风格的自由连线思维导图编辑器（跟随主程序主题）。</summary>
public partial class MindmapWindow : Window
{
    private MindmapDoc _doc = new();
    private string? _path;
    private readonly Dictionary<string, Border> _nodeVisuals = new();
    private bool _dirty;

    // 画布
    private bool _panning;
    private Point _lastPanPt;
    private double _zoom = 1.0;

    // 节点
    private Border? _dragNode;
    private double _dragOffX, _dragOffY;
    private DateTime _lastNodeDown = DateTime.MinValue;
    private Border? _lastNodeDownBorder;
    private bool _suppressClickEdit;

    // 连线：从节点的连接点向外拖
    private Border? _linkFrom;
    private Point _linkCur;
    private bool _linking;

    // 空白双击检测
    private DateTime _lastViewportDown = DateTime.MinValue;
    private Point _lastViewportDownPt;

    private static readonly string _filter = "连连看图 (*.llk)|*.llk|JSON (*.json)|*.json|所有文件 (*.*)|*.*";

    public MindmapWindow()
    {
        InitializeComponent();
        ApplyTheme();
        NewDocument();
    }

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
        int delta = theme.IsDark ? -16 : 12;
        var cv = theme.CardTint;
        Resources["CanvasBg"] = new SolidColorBrush(Color.FromRgb(ClampByte(cv.R + delta), ClampByte(cv.G + delta), ClampByte(cv.B + delta)));
    }
    private static byte ClampByte(int v) => (byte)Math.Clamp(v, 0, 255);

    // ==================== 文档 ====================
    public void NewDocument()
    {
        _doc = new MindmapDoc();
        _path = null; _dirty = false;
        _doc.Nodes.Add(new MindNode { Text = "中心主题", X = 0, Y = 0, W = 130 });
        _doc.Nodes.Add(new MindNode { Text = "分支 1", X = 250, Y = -90 });
        _doc.Nodes.Add(new MindNode { Text = "分支 2", X = 250, Y = 100 });
        _doc.Links.Add(new MindLink { From = _doc.Nodes[0].Id, To = _doc.Nodes[1].Id });
        _doc.Links.Add(new MindLink { From = _doc.Nodes[0].Id, To = _doc.Nodes[2].Id });
        Rebuild(); UpdateTitle();
    }
    public void LoadPath(string path) { _doc = MindmapDoc.Load(path); _path = path; _dirty = false; Rebuild(); UpdateTitle(); AddRecent(path); }

    private void OnNew(object s, RoutedEventArgs e) => EnsureSaved(NewDocument);
    private void OnOpen(object s, RoutedEventArgs e) => EnsureSaved(() => {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = _filter, Title = "打开连连看" };
        if (dlg.ShowDialog(this) == true) LoadPath(dlg.FileName);
    });
    private void OnSave(object s, RoutedEventArgs e) => Save();
    private void OnHistory(object s, RoutedEventArgs e) => ShowHistory();
    private void OnClose(object s, RoutedEventArgs e) => EnsureSaved(Close);

    private void Save()
    {
        if (_path == null) ChooseDirAndSave();
        else { _doc.Save(_path); _dirty = false; AddRecent(_path); UpdateTitle(); }
    }
    private void ChooseDirAndSave()
    {
        var cfg = ConfigManager.Load();
        if (string.IsNullOrWhiteSpace(cfg.MindmapDir) || !Directory.Exists(cfg.MindmapDir!))
        {
            var folder = new Microsoft.Win32.OpenFolderDialog { Title = "选择连连看保存目录（首次使用）" };
            if (folder.ShowDialog(this) != true) return;
            cfg.MindmapDir = folder.FolderName;
            ConfigManager.Save(cfg);
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
    private void AddRecent(string file)
    {
        try {
            var cfg = ConfigManager.Load(); cfg.RecentMindmaps.Remove(file); cfg.RecentMindmaps.Insert(0, file);
            if (cfg.RecentMindmaps.Count > 20) cfg.RecentMindmaps.RemoveRange(20, cfg.RecentMindmaps.Count - 20);
            ConfigManager.Save(cfg);
        } catch { }
    }
    private void ShowHistory()
    {
        var cfg = ConfigManager.Load();
        var list = cfg.RecentMindmaps.Where(File.Exists).ToList();
        if (list.Count == 0) { MessageBox.Show(this, "暂无历史记录。", "历史"); return; }
        var menu = new ContextMenu();
        foreach (var f in list.Take(12))
            menu.Items.Add(new MenuItem { Header = PathIO.GetFileName(f), ToolTip = f, Tag = f });
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "清空历史" };
        clear.Click += (_, _) => { cfg.RecentMindmaps.Clear(); ConfigManager.Save(cfg); };
        menu.Items.Add(clear);
        foreach (var it in menu.Items.OfType<MenuItem>().Where(x => x.Tag is string))
            it.Click += (s, _) => EnsureSaved(() => LoadPath((string)((MenuItem)s!).Tag!));
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
        var border = new Border { Tag = n, Background = new SolidColorBrush(ColorFromHex(n.Color)), CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), Padding = new Thickness(10, 7, 10, 7), Cursor = Cursors.Hand };
        var text = new TextBlock { Text = n.Text, FontSize = 13, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
        border.Child = text;
        Canvas.SetLeft(border, n.X); Canvas.SetTop(border, n.Y);
        border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        n.W = Math.Max(80, border.DesiredSize.Width + 20); border.Width = n.W;
        border.MouseLeftButtonDown += OnNodeDown;
        border.MouseLeftButtonUp += OnNodeUp;
        border.MouseMove += OnNodeMove;
        border.PreviewMouseLeftButtonDown += OnNodePreviewDown;
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
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(Center(a), false, false);
                var mx = Center(a).X + (Center(b).X - Center(a).X) / 2;
                ctx.BezierTo(new Point(mx, Center(a).Y), new Point(mx, Center(b).Y), Center(b), true, false);
            }
            g.Freeze();
            LinkCanvas.Children.Add(new System.Windows.Shapes.Path { Stroke = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1.6, Data = g });
        }
    }
    private Point Center(Border b) => new Point(Canvas.GetLeft(b) + b.ActualWidth / 2, Canvas.GetTop(b) + b.ActualHeight / 2);

    // ==================== 节点交互 ====================

    private void OnNodePreviewDown(object sender, MouseButtonEventArgs e)
    {
        // 双击检测：第二次按下若与前次间隔<350ms 且同一节点 → 转编辑，不进入拖拽
        if (_lastNodeDownBorder == sender && (DateTime.UtcNow - _lastNodeDown).TotalMilliseconds < 350)
        {
            _suppressClickEdit = true;
            _dragNode = null;
            var n = (MindNode)((Border)sender).Tag;
            EditNodeText(n);
            _lastNodeDown = DateTime.MinValue; _lastNodeDownBorder = null;
            e.Handled = true;
            return;
        }
        _lastNodeDown = DateTime.UtcNow; _lastNodeDownBorder = sender as Border; _suppressClickEdit = false;
    }

    private void OnNodeDown(object sender, MouseButtonEventArgs e)
    {
        var b = (Border)sender;
        _dragNode = b;
        var pos = e.GetPosition(NodeCanvas);
        _dragOffX = pos.X - Canvas.GetLeft(b); _dragOffY = pos.Y - Canvas.GetTop(b);
        b.CaptureMouse();
        e.Handled = true;
    }
    private void OnNodeMove(object sender, MouseEventArgs e)
    {
        if (_dragNode != sender || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(NodeCanvas);
        Canvas.SetLeft(_dragNode, pos.X - _dragOffX); Canvas.SetTop(_dragNode, pos.Y - _dragOffY);
        RedrawLinks(); _dirty = true;
    }
    private void OnNodeUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragNode == sender) _dragNode = null;
        ((Border)sender).ReleaseMouseCapture();
    }

    // ==================== 编辑 / 删除 / 颜色 ====================

    private void EditNodeText(MindNode n)
    {
        if (!_nodeVisuals.TryGetValue(n.Id, out var b)) return;
        var box = new TextBox { Text = n.Text, FontSize = 13, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(0x44, 0, 0, 0)), BorderThickness = new Thickness(0), Padding = new Thickness(6,4,6,4) };
        b.Child = box; box.Focus(); box.SelectAll();
        box.KeyDown += (_, e) => {
            if (e.Key == Key.Enter) { n.Text = box.Text.Trim(); if (n.Text.Length == 0) n.Text = "新节点"; Rebuild(); _dirty = true; e.Handled = true; }
            else if (e.Key == Key.Escape) { Rebuild(); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => { if (box.Text != n.Text) { n.Text = string.IsNullOrWhiteSpace(box.Text) ? n.Text : box.Text.Trim(); _dirty = true; } Rebuild(); };
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

    // ==================== 连线交互（从节点拖出） ====================

    private void StartLink(MindNode n)
    {
        if (!_nodeVisuals.TryGetValue(n.Id, out var b)) return;
        _linkFrom = b; _linkCur = Center(b); _linking = true;
        Viewport.CaptureMouse();
        RedrawLinks(); RedrawLinkPreview();
    }

    // ==================== 画布（空白双击新建 / 平移 / 连线抬落） ====================

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // 空白双击 → 在该处新建节点
        if ((DateTime.UtcNow - _lastViewportDown).TotalMilliseconds < 350 &&
            (e.GetPosition(Viewport) - _lastViewportDownPt).Length < 6)
        {
            var canvasPos = e.GetPosition(NodeCanvas);
            var n = new MindNode { Text = "新节点", X = canvasPos.X - 55, Y = canvasPos.Y - 16 };
            _doc.Nodes.Add(n); Rebuild(); _dirty = true; EditNodeText(n);
            _lastViewportDown = DateTime.MinValue;
            e.Handled = true;
            return;
        }
        _lastViewportDown = DateTime.UtcNow; _lastViewportDownPt = e.GetPosition(Viewport);
        // 开始平移（确保没点中节点，节点事件已设 Handled）
        _panning = true; _lastPanPt = e.GetPosition(Viewport); Viewport.CaptureMouse();
        e.Handled = true;
    }
    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_linking && _linkFrom != null)
        {
            _linkCur = e.GetPosition(NodeCanvas);
            RedrawLinks(); RedrawLinkPreview();
        }
        else if (_panning && e.LeftButton == MouseButtonState.Pressed)
        {
            var p = e.GetPosition(Viewport); var d = p - _lastPanPt; _lastPanPt = p;
            PanTransform.X += d.X; PanTransform.Y += d.Y;
        }
    }
    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_linking && _linkFrom != null)
        {
            var target = HitTestNode(e.GetPosition(NodeCanvas));
            if (target != null && !ReferenceEquals(target, _linkFrom) && !_doc.Links.Any(x => (x.From == GetNode(_linkFrom) && x.To == GetNode(target)) ))
            {
                _doc.Links.Add(new MindLink { From = GetNode(_linkFrom), To = GetNode(target) });
                _dirty = true;
            }
        }
        _linking = false; _linkFrom = null; Viewport.ReleaseMouseCapture(); _panning = false;
        RedrawLinks();
    }
    private void OnViewportMouseLeave(object sender, MouseEventArgs e) { if (_linking) { _linking = false; _linkFrom = null; RedrawLinks(); } }

    private string GetNode(Border b) => ((MindNode)b.Tag!).Id;

    private void RedrawLinkPreview()
    {
        foreach (System.Windows.Shapes.Path p in LinkCanvas.Children.OfType<System.Windows.Shapes.Path>().Where(x => x.Tag?.ToString() == "preview").ToList()) LinkCanvas.Children.Remove(p);
        if (_linkFrom == null) return;
        var p1 = Center(_linkFrom);
        var line = new Line { X1 = p1.X, Y1 = p1.Y, X2 = _linkCur.X, Y2 = _linkCur.Y,
            Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0x4A, 0x90, 0xFF)), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 } };
        Canvas.SetZIndex(line, 999); LinkCanvas.Children.Add(line);
    }

    private Border? HitTestNode(Point pt)
    {
        foreach (var b in _nodeVisuals.Values)
        {
            var left = Canvas.GetLeft(b); var top = Canvas.GetTop(b);
            if (pt.X >= left && pt.X <= left + b.ActualWidth && pt.Y >= top && pt.Y <= top + b.ActualHeight) return b;
        }
        return null;
    }

    // ==================== 缩放 / 键盘 ====================
    private void OnZoomIn(object s, RoutedEventArgs e) => SetZoom(_zoom * 1.2);
    private void OnZoomOut(object s, RoutedEventArgs e) => SetZoom(_zoom / 1.2);
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e) { SetZoom(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15)); e.Handled = true; }
    private void SetZoom(double z) { _zoom = Math.Clamp(z, 0.25, 3.0); ZoomTransform.ScaleX = ZoomTransform.ScaleY = _zoom; ZoomText.Text = (int)(_zoom * 100) + "%"; }

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