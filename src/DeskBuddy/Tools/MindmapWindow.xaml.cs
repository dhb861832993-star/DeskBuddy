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
    private string? _path;                       // 当前存档路径（null=未保存）
    private readonly Dictionary<string, Border> _nodeVisuals = new();
    private bool _dirty;

    // 画布交互状态
    private bool _panning;
    private Point _lastPanPt;
    private double _zoom = 1.0;

    // 节点拖拽状态
    private Border? _dragNode;
    private double _dragOffsetX, _dragOffsetY;

    // 连线状态：从一个节点向另一个节点拉线
    private Border? _linkStart;
    private Point _linkCur;

    private readonly Random _rnd = new();

    /// <summary>流程：用于弹「打开/保存/选目录」对话框。</summary>
    private static readonly string _filter = "连连看图 (*.llk)|*.llk|JSON (*.json)|*.json|所有文件 (*.*)|*.*";

    public MindmapWindow()
    {
        ApplyTheme(); // 与主界面同一套主题
        InitializeComponent();
    }

    /// <summary>注入主程序主题资源，保证 UI 风格统一、随系统深浅色。</summary>
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private void ApplyTheme()
    {
        var theme = Theme.From(Services.ConfigManager.Load().Theme);
        Resources["TextPrimary"] = Frozen(theme.TextPrimary);
        Resources["TextSecondary"] = Frozen(theme.TextSecondary);
        Resources["HoverBg"] = Frozen(theme.HoverBg);
        Resources["CardBorder"] = Frozen(theme.BorderColor);
        Resources["BtnBg"] = Frozen(theme.HoverBg);
        Resources["BtnBgHover"] = Frozen(Color.FromArgb(0x3D, theme.HoverBg.R, theme.HoverBg.G, theme.HoverBg.B));
        Resources["CardBg"] = new SolidColorBrush(theme.CardTint) { Opacity = theme.CardAlpha };
        // 画布比卡片卡片略深（深色）或略浅（浅色），形成层次
        int delta = theme.IsDark ? -16 : 12;
        var cv = theme.CardTint;
        var canvas = Color.FromRgb(
            ClampByte(cv.R + delta), ClampByte(cv.G + delta), ClampByte(cv.B + delta));
        Resources["CanvasBg"] = new SolidColorBrush(canvas);
    }

    private static byte ClampByte(int v) => (byte)Math.Clamp(v, 0, 255);

    // ==================== 文档生命周期 ====================

    public void NewDocument()
    {
        _doc = new MindmapDoc();
        _path = null;
        _dirty = false;
        // 初始：一个中心主题 + 两个分支
        _doc.Nodes.Add(new MindNode { Text = "中心主题", X = 0, Y = 0, W = 130 });
        _doc.Nodes.Add(new MindNode { Text = "分支 1", X = 260, Y = -80 });
        _doc.Nodes.Add(new MindNode { Text = "分支 2", X = 260, Y = 90 });
        _doc.Links.Add(new MindLink { From = _doc.Nodes[0].Id, To = _doc.Nodes[1].Id });
        _doc.Links.Add(new MindLink { From = _doc.Nodes[0].Id, To = _doc.Nodes[2].Id });
        Rebuild();
        UpdateTitle();
    }

    public void LoadPath(string path)
    {
        _doc = MindmapDoc.Load(path);
        _path = path;
        _dirty = false;
        Rebuild();
        UpdateTitle();
    }

    private void OnNew(object s, RoutedEventArgs e) { EnsureSaved(() => NewDocument()); }
    private void OnOpen(object s, RoutedEventArgs e)
    {
        EnsureSaved(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = _filter, Title = "打开连连看" };
            if (dlg.ShowDialog(this) == true) { LoadPath(dlg.FileName); AddRecent(dlg.FileName); }
        });
    }
    private void OnSave(object s, RoutedEventArgs e) => Save();
    private void OnHistory(object s, RoutedEventArgs e) => ShowHistory();
    private void OnClose(object s, RoutedEventArgs e) { EnsureSaved(() => Close()); }

    private void Save()
    {
        if (_path == null) ChooseDirAndSave();
        else { _doc.Save(_path); _dirty = false; AddRecent(_path); UpdateTitle(); }
    }

    /// <summary>首次使用：确定保存目录与文件名。</summary>
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
        if (string.IsNullOrEmpty(dlg.FileName)) return;
        // 确保扩展名 .llk
        var file = PathIO.GetExtension(dlg.FileName).ToLowerInvariant() == ".llk" ? dlg.FileName : dlg.FileName + ".llk";
        _doc.Save(file);
        _path = file;
        _dirty = false;
        AddRecent(file);
        UpdateTitle();
    }

    private void EnsureSaved(Action cont)
    {
        if (!_dirty) { cont(); return; }
        var r = MessageBox.Show(this, "有未保存的修改，是否保存？", "连连看",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) { if (SaveAndReturn()) cont() ; }
        else if (r == MessageBoxResult.No) cont();
        // Cancel: 不执行 cont
    }

    private bool SaveAndReturn()
    {
        var hadPath = _path != null;
        Save();
        return _path != null || !hadPath; // 保存成功或用户取消了保存对话框则视为可继续
    }

    private void UpdateTitle()
    {
        DocNameText.Text = _path == null ? "未命名" : PathIO.GetFileName(_path);
        Title = "连连看 — " + DocNameText.Text;
    }

    private void AddRecent(string file)
    {
        try
        {
            var cfg = ConfigManager.Load();
            cfg.RecentMindmaps.Remove(file);
            cfg.RecentMindmaps.Insert(0, file);
            if (cfg.RecentMindmaps.Count > 20) cfg.RecentMindmaps.RemoveRange(20, cfg.RecentMindmaps.Count - 20);
            ConfigManager.Save(cfg);
        }
        catch { }
    }

    private void ShowHistory()
    {
        var cfg = ConfigManager.Load();
        var list = cfg.RecentMindmaps.Where(f => File.Exists(f)).ToList();
        if (list.Count == 0) { MessageBox.Show(this, "暂无历史记录。", "历史"); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = _filter, Title = "最近打开的连连看",
            InitialDirectory = PathIO.GetDirectoryName(list[0])
        };
        // 简化：用自定义菜单列历史
        var menu = new ContextMenu();
        foreach (var f in list.Take(12))
            menu.Items.Add(new MenuItem { Header = PathIO.GetFileName(f) + "  (" + PathIO.GetDirectoryName(f) + ")", Tag = f });
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "清空历史" };
        clear.Click += (_, _) => { cfg.RecentMindmaps.Clear(); ConfigManager.Save(cfg); };
        menu.Items.Add(clear);
        menu.IsOpen = true;
        foreach (var it in menu.Items.OfType<MenuItem>().Where(x => x.Tag is string))
            it.Click += (s, _) => { EnsureSaved(() => { LoadPath((string)((MenuItem)s!).Tag); }); };
    }

    // ==================== 渲染 ====================

    private void Rebuild()
    {
        NodeCanvas.Children.Clear();
        _nodeVisuals.Clear();
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
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            Cursor = Cursors.Hand,
            MinWidth = 60
        };
        var text = new TextBlock { Text = n.Text, FontSize = 13, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
        border.Child = text;
        Canvas.SetLeft(border, n.X);
        Canvas.SetTop(border, n.Y);
        border.Width = n.W;
        border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        n.W = border.DesiredSize.Width + 20;
        border.Width = n.W;
        border.MouseLeftButtonDown += (_, e) => OnNodeDown(border, e);
        border.MouseLeftButtonUp += (_, e) => OnNodeUp(border, e);
        border.MouseMove += (_, e) => OnNodeMove(border, e);
        border.MouseRightButtonDown += (_, e) => OnNodeRight(border, e);
        _nodeVisuals[n.Id] = border;
        NodeCanvas.Children.Add(border);
    }

    private Color ColorFromHex(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Color.FromArgb(0x22, 0x4A, 0x90, 0xFF); }
    }

    private void RedrawLinks()
    {
        LinkCanvas.Children.Clear();
        foreach (var l in _doc.Links)
        {
            if (!_nodeVisuals.TryGetValue(l.From, out var a) || !_nodeVisuals.TryGetValue(l.To, out var b)) continue;
            var p1 = Center(a); var p2 = Center(b);
            // 贝塞尔曲线
            var path = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1.6,
                Data = new StreamGeometry()
            };
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(p1, false, false);
                var mx = p1.X + (p2.X - p1.X) / 2;
                ctx.BezierTo(new Point(mx, p1.Y), new Point(mx, p2.Y), p2, true, false);
            }
            g.Freeze();
            path.Data = g;
            LinkCanvas.Children.Add(path);
        }
    }

    private Point Center(Border b)
    {
        double x = Canvas.GetLeft(b), y = Canvas.GetTop(b);
        return new Point(x + b.ActualWidth / 2, y + b.ActualHeight / 2);
    }

    // ==================== 节点交互 ====================

    private void OnNodeDown(Border b, MouseButtonEventArgs e)
    {
        _dragNode = b;
        var pos = e.GetPosition(NodeCanvas);
        _dragOffsetX = pos.X - Canvas.GetLeft(b);
        _dragOffsetY = pos.Y - Canvas.GetTop(b);
        b.CaptureMouse();
        e.Handled = true;
    }

    private void OnNodeMove(Border b, MouseEventArgs e)
    {
        if (_dragNode != b || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(NodeCanvas);
        Canvas.SetLeft(b, pos.X - _dragOffsetX);
        Canvas.SetTop(b, pos.Y - _dragOffsetY);
        RedrawLinks();
        _dirty = true;
    }

    private void OnNodeUp(Border b, MouseButtonEventArgs e)
    {
        if (_dragNode == b) _dragNode = null;
        b.ReleaseMouseCapture();
    }

    private void OnNodeRight(Border b, MouseButtonEventArgs e)
    {
        var n = (MindNode)b.Tag;
        var menu = new ContextMenu();
        var link = new MenuItem { Header = "连线到…" };
        link.Click += (_, _) => StartLink(b);
        var edit = new MenuItem { Header = "编辑文字" };
        edit.Click += (_, _) => EditNodeText(b);
        var del = new MenuItem { Header = "删除节点" };
        del.Click += (_, _) => { _doc.Nodes.RemoveAll(x => x.Id == n.Id); _doc.Links.RemoveAll(x => x.From == n.Id || x.To == n.Id); Rebuild(); _dirty = true; };
        menu.Items.Add(link); menu.Items.Add(edit); menu.Items.Add(del);
        menu.IsOpen = true;
    }

    private void EditNodeText(Border b)
    {
        var n = (MindNode)b.Tag;
        var container = new Grid();
        var box = new TextBox { Text = n.Text, FontSize = 13, Background = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)), Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(6,4,6,4) };
        b.Child = container;
        container.Children.Add(box);
        box.Focus(); box.SelectAll();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { n.Text = box.Text.Trim(); Rebuild(); _dirty = true; e.Handled = true; }
            else if (e.Key == Key.Escape) { Rebuild(); e.Handled = true; }
        };
    }

    // ==================== 连线交互 ====================

    private void StartLink(Border b)
    {
        _linkStart = b;
        _linkCur = Center(b);
        LinkCanvas.IsHitTestVisible = true;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        SetZoom(_zoom * factor);
        e.Handled = true;
    }

    // ==================== 画布平移（拖空白） ====================

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _linkStart == null)
        {
            _panning = true; _lastPanPt = e.GetPosition(Viewport); Viewport.CaptureMouse();
        }
    }
    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_linkStart != null)
        {
            _linkCur = e.GetPosition(Viewport);
            RedrawLinkPreview();
        }
        if (_panning && e.LeftButton == MouseButtonState.Pressed)
        {
            var p = e.GetPosition(Viewport);
            var d = p - _lastPanPt; _lastPanPt = p;
            PanTransform.X += d.X; PanTransform.Y += d.Y;
        }
    }
    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning) { _panning = false; Viewport.ReleaseMouseCapture(); }
        if (_linkStart != null)
        {
            // 完成连线：落在某个节点上（用 NodeCanvas 坐标系，避免缩放/平移偏差）
            var hitBorder = HitTestNode(e.GetPosition(NodeCanvas));
            if (hitBorder != null && !ReferenceEquals(hitBorder, _linkStart))
            {
                _doc.Links.Add(new MindLink { From = GetNode(_linkStart), To = GetNode(hitBorder) });
                _dirty = true;
            }
            _linkStart = null;
            LinkCanvas.IsHitTestVisible = false;
            RedrawLinks();
        }
    }

    private string GetNode(Border b) => ((MindNode)b.Tag!).Id;

    private void RedrawLinkPreview()
    {
        LinkCanvas.Children.Clear();
        if (_linkStart == null) return;
        RedrawLinks();
        var p1 = Center(_linkStart);
        var line = new Line { X1 = p1.X, Y1 = p1.Y, X2 = _linkCur.X, Y2 = _linkCur.Y,
                              Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0x4A, 0x90, 0xFF)), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 } };
        Canvas.SetZIndex(line, 999);
        LinkCanvas.Children.Add(line);
    }

    private Border? HitTestNode(Point pt)
    {
        foreach (var b in _nodeVisuals.Values)
        {
            var left = Canvas.GetLeft(b);
            var top = Canvas.GetTop(b);
            if (pt.X >= left && pt.X <= left + b.ActualWidth && pt.Y >= top && pt.Y <= top + b.ActualHeight) return b;
        }
        return null;
    }

    // ==================== 缩放按钮 ====================
    private void OnZoomIn(object s, RoutedEventArgs e) => SetZoom(_zoom * 1.2);
    private void OnZoomOut(object s, RoutedEventArgs e) => SetZoom(_zoom / 1.2);

    private void SetZoom(double z)
    {
        _zoom = Math.Clamp(z, 0.25, 3.0);
        ZoomTransform.ScaleX = ZoomTransform.ScaleY = _zoom;
        ZoomText.Text = (int)(_zoom * 100) + "%";
    }

    // ==================== 窗口键盘 ====================
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { }
        else if (e.Key == Key.Escape && _linkStart != null) { _linkStart = null; RedrawLinks(); }
    }

    // ==================== 关闭拦截 ====================
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