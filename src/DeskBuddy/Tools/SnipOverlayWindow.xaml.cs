using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Shapes;
using System.Windows.Threading;
using SD = System.Drawing;
using DeskBuddy.Services;

namespace DeskBuddy.Tools;

/// <summary>
/// Snipaste 式截图覆盖层（多屏混合 DPI 正确版）。
/// 方案：每个显示器一个独立覆盖窗口（各自 DPI 各自 1:1 物理像素显示），
/// 所有选区逻辑用「虚拟屏 DIP」坐标，窗口只负责各自屏内的显示与命中。
/// </summary>
public partial class SnipOverlayWindow : Window
{
    // ===== 每屏一窗 =====
    private sealed class MonWindow
    {
        public required Window Win;
        public required Image Img;
        public required Canvas Canvas;
        public Rect Dip;                   // 全局虚拟 DIP 矩形（主屏 DPI 基准）
        public double MainDpi;            // 主屏 DPI（全局 DIP 基准）
        public SD.Rectangle Phys;         // 物理像素矩形
    }
    private readonly List<MonWindow> _mw = new();

    // ===== 冻结层 =====
    private SD.Bitmap? _bmp;              // 全虚拟屏 DIP 网格（供取色/输出）
    private byte[]? _px; private int _bw, _bh, _stride;
    private double _winW, _winH;           // 虚拟屏 DIP 尺寸
    private double _vsX, _vsY;

    // ===== 选区（虚拟屏 DIP） =====
    private Rect _sel = Rect.Empty;
    private bool _hasSel;

    // ===== 交互 =====
    private bool _drawing; private Point _drawStart;
    private int _dragKind; private Point _dragStartPt; private Rect _dragStartSel;

    // ===== 可视元素（画在每屏 Canvas 上，跨屏各画各的） =====
    private readonly Rectangle[] _masks = new Rectangle[4];
    private readonly Rectangle _border = new();
    private readonly Rectangle[] _handles = new Rectangle[8];
    private readonly TextBlock _sizeLabel = new();
    private Border? _sizeBg;
    private Border? _toolbar;
    private readonly Canvas _loupe = new();
    private readonly Rectangle[] _loupeCells = new Rectangle[LoupeN * LoupeN];
    private readonly SolidColorBrush[] _loupeBrushes = new SolidColorBrush[LoupeN * LoupeN];
    private readonly Rectangle _loupeCenter = new();
    private readonly TextBlock _loupeText = new();
    private string _lastLoupeText = "";
    private Border? _loupePanel;
    private const int LoupeN = 9;
    private const int CellPx = 12;

    // 选区所有者（哪个屏的 Canvas 上有操作条/放大镜）
    private MonWindow? _uiHost;

    // ==================== 激活入口（可靠优先：每次新建，秒级可接受） ====================

    /// <summary>静态持有当前实例（App 保险用）。</summary>
    public static SnipOverlayWindow? _pooled;

    /// <summary>App 启动时预创建（预热 JIT/视觉树——窗口不实际建，避免 WPF 隐藏窗口的状态坑）。</summary>
    public static void PreCreate()
    {
        // 仅预热：跑一遍枚举 + 构造函数（JIT 编译），不显示任何窗口
        try
        {
            var warm = new SnipOverlayWindow(precreate: true);
            warm.EnumPhys();
        }
        catch { }
    }

    /// <summary>F1 触发：新建覆盖层（每屏新窗口，显示状态机全新 → 绝对可靠）。</summary>
    public static SnipOverlayWindow Activate()
    {
        var win = new SnipOverlayWindow(precreate: true);
        win.BuildAllAndShow();
        _pooled = win;
        return win;
    }

    /// <summary>真实激活态（App 保险用：状态卡死时自动恢复）。</summary>
    public static bool IsReallyActive() => _pooled?._active == true;

    private bool _precreated;
    private bool _active;

    public SnipOverlayWindow() : this(precreate: false) { }

    private SnipOverlayWindow(bool precreate)
    {
        _precreated = precreate;
        // 本窗口是主逻辑宿主（不可见），真实覆盖是每屏的子窗口
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = 0; Height = 0;
        Opacity = 0;
        IsHitTestVisible = false;
        if (precreate)
        {
            InitVisuals();
        }
        Loaded += (s, e) =>
        {
            Focusable = true;
        };
    }

    /// <summary>新建实例激活：截屏 → 建窗口 → 显示（每轮全新状态机，无复用坑）。</summary>
    private void BuildAllAndShow()
    {
        _active = true;
        try
        {
            BuildMonitorWindows(capture: true);   // 建窗即显示（全新窗口，Show 可靠）
            foreach (var m in _mw) m.Win.Activate();
            // 重活后置
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try { BuildPixelGrid(); } catch { }
            }));
        }
        catch { _active = false; }
    }

    /// <summary>激活：截屏换图 → Show() 显示（与预创建的 Show→Hide 对称，状态机可靠）。</summary>
    public void Start()
    {
        DebugLog.Write($"[SNIP] Start() called, _active={_active}, _mw.Count={_mw.Count}");
        if (_active) return;
        _active = true;
        try
        {
            RefreshScreens();          // 只截屏 + 换图，不建窗
            foreach (var m in _mw)
            {
                m.Win.Show();          // WPF 正路显示
                m.Win.Activate();
            }
            Focusable = true;
            // 重活后置
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try { BuildPixelGrid(); } catch { }
            }));
        }
        catch { _active = false; }
    }

    /// <summary>结束（Stop/CloseAll）时通知外部重置激活态。</summary>
    public event Action? StopRequested;

    /// <summary>结束：Hide() 隐藏（与 Show 对称；不销毁，下次秒开）。</summary>
    public void Stop()
    {
        _active = false;
        _hasSel = false; _sel = Rect.Empty;
        _drawing = false; _dragKind = 0;
        foreach (var m in _mw)
        {
            try { m.Win.Hide(); } catch { }
            try { m.Canvas.Children.Clear(); } catch { }
            if (_toolbar?.Parent is Panel p) p.Children.Remove(_toolbar);
        }
        _bmp?.Dispose(); _bmp = null; _px = null;
        foreach (var s in _shots) { try { s.Bmp?.Dispose(); } catch { } }
        _shots.Clear();
        DebugLog.Write("[SNIP] Stop() called, _active=false, invoking StopRequested");
        try { StopRequested?.Invoke(); } catch (Exception ex) { DebugLog.Write($"[SNIP] StopRequested ex: {ex.Message}"); }
    }

    /// <summary>结束并真正关闭（每次新建模式：关掉所有窗口 + 宿主，状态彻底清零）。</summary>
    public void StopAndClose()
    {
        Stop();
        foreach (var m in _mw) { try { m.Win.Close(); } catch { } }
        if (ReferenceEquals(_pooled, this)) _pooled = null;
        try { Close(); } catch { }
    }

    // ==================== Win32 ====================
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumProc lpfnEnum, IntPtr dwData);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    private static bool CollectPhys(IntPtr hMon, IntPtr hdcMon, ref RECT r, IntPtr data)
    {
        var act = (Action<RECT>)GCHandle.FromIntPtr(data).Target!;
        act(r);
        return true;
    }

    // ==================== 截屏（预创建窗口 + 激活换图） ====================
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_HIDE = 0, SW_SHOW = 5, SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_HIDEWINDOW = 0x0080;
    private readonly List<IntPtr> _hwnds = new();

    private readonly List<(MonWindow MW, SD.Bitmap Bmp)> _shots = new();
    private List<SD.Rectangle> _physRects = new();
    private double _mainDpi = 1.5;

    /// <summary>枚举物理屏（预创建时算好，激活时直接复用）。</summary>
    private void EnumPhys()
    {
        _physRects = new List<SD.Rectangle>();
        var act = (Action<RECT>)(r => _physRects.Add(new SD.Rectangle(r.L, r.T, r.R - r.L, r.B - r.T)));
        var gc = GCHandle.Alloc(act);
        try { EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, CollectPhys, GCHandle.ToIntPtr(gc)); }
        finally { gc.Free(); }
        var mainPhys = _physRects.FirstOrDefault(r => r.X == 0 && r.Y == 0);
        var mainDipW = SystemParameters.PrimaryScreenWidth;
        _mainDpi = (mainPhys.Width > 0 && mainDipW > 0) ? mainPhys.Width / mainDipW : 1.0;
    }

    /// <summary>预创建：只建窗口骨架（每屏子窗口 + 事件），不截屏。
    /// 显示管理全走 Win32（WPF 的 Visibility/Show/Hide 互相打架，是 F1 失效的根因）。</summary>
    private void BuildMonitorWindows(bool capture)
    {
        EnumPhys();   // 无论 capture 与否都先枚举（否则首激活误判布局变更）
        _mw.Clear(); _shots.Clear(); _hwnds.Clear();
        int wi = 0;
        foreach (var phys in _physRects)
        {
            var dip = new Rect(phys.X / _mainDpi, phys.Y / _mainDpi, phys.Width / _mainDpi, phys.Height / _mainDpi);
            var win = new Window
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                ResizeMode = ResizeMode.NoResize,
                Cursor = Cursors.Cross,
                Background = Brushes.Black,
            };
            var img = new Image { Stretch = Stretch.Fill };
            var canvas = new Canvas();
            var grid = new Grid();
            grid.Children.Add(img); grid.Children.Add(canvas);
            win.Content = grid;
            win.PreviewMouseLeftButtonDown += OnMouseLeftDown;
            win.PreviewMouseMove += OnMouseMove;
            win.PreviewMouseLeftButtonUp += OnMouseLeftUp;
            win.PreviewKeyDown += OnWindowKeyDown;
            var isCapture = capture;   // 闭包捕获
            win.SourceInitialized += (s2, e2) =>
            {
                var h = new WindowInteropHelper(win).Handle;
                // 摆好位置 + 顶置（隐藏态由 WPF Visibility 管理，不走 Win32 Hide）
                SetWindowPos(h, new IntPtr(-1) /*HWND_TOPMOST*/, phys.X, phys.Y, phys.Width, phys.Height,
                    SWP_NOACTIVATE | (isCapture ? SWP_SHOWWINDOW : 0));
                _hwnds.Add(h);
            };
            if (capture)
            {
                win.Show();
            }
            else
            {
                // 预创建：完整走一遍 WPF 显示状态机（Show→Hide），
                // 之后 Show()/Hide() 才能对称可靠（先设 Hidden 再 Show 会让 Visible 切换失效——WPF 内部状态坑）
                win.Show();
                win.Hide();
            }
            var mw = new MonWindow { Win = win, Img = img, Canvas = canvas, Dip = dip, MainDpi = _mainDpi, Phys = phys };
            _mw.Add(mw);
            _shots.Add((mw, null!));
            wi++;
        }
        // 统一虚拟 DIP 视界
        var allX = _mw.Select(m => m.Dip.X).DefaultIfEmpty(0).Min();
        var allY = _mw.Select(m => m.Dip.Y).DefaultIfEmpty(0).Min();
        _vsX = allX; _vsY = allY;
        _winW = _mw.Select(m => m.Dip.Right).DefaultIfEmpty(0).Max() - allX;
        _winH = _mw.Select(m => m.Dip.Bottom).DefaultIfEmpty(0).Max() - allY;
    }

    /// <summary>激活：截屏换图 → 显示（窗口早已建好，只剩截屏 190ms）。</summary>
    private void RefreshScreens()
    {
        // 屏幕布局变更检测：仅当之前有布局且真正变化时才重建（首激活 _physRects 为空 = 直接补枚举）
        static string Fingerprint(List<SD.Rectangle> rs) =>
            string.Join("|", rs.OrderBy(r => r.X).ThenBy(r => r.Y).Select(r => $"{r.X},{r.Y},{r.Width}x{r.Height}"));
        var oldFp = _physRects.Count > 0 ? Fingerprint(_physRects) : null;
        EnumPhys();
        var newFp = Fingerprint(_physRects);
        if ((oldFp != null && oldFp != newFp) || _mw.Count == 0)
        {
            foreach (var m in _mw) { try { m.Win.Close(); } catch { } }
            _hwnds.Clear();
            BuildMonitorWindows(capture: false);
            DebugLog.Write("[SNIP] layout changed -> windows rebuilt");
        }
        // 截屏换图
        for (int i = 0; i < _physRects.Count && i < _mw.Count; i++)
        {
            var phys = _physRects[i];
            var bmp = new SD.Bitmap(phys.Width, phys.Height);
            using (var g = SD.Graphics.FromImage(bmp))
                g.CopyFromScreen(phys.X, phys.Y, 0, 0, new SD.Size(phys.Width, phys.Height));
            _mw[i].Img.Source = ToSource(bmp);
            _shots[i] = (_mw[i], bmp);
        }
        DebugLog.Write($"[SNIP] refreshed {_mw.Count} screens (windows reused)");
    }

    /// <summary>取色/输出用的全屏 DIP 网格位图 + 像素缓存（后台构建，不挡截屏显示）。</summary>
    private void BuildPixelGrid()
    {
        _bw = Math.Max(1, (int)Math.Round(_winW));
        _bh = Math.Max(1, (int)Math.Round(_winH));
        _bmp = new SD.Bitmap(_bw, _bh);
        using (var bg = SD.Graphics.FromImage(_bmp))
        {
            foreach (var (m, mb) in _shots)
            {
                var dx = (float)(m.Dip.X - _vsX);
                var dy = (float)(m.Dip.Y - _vsY);
                bg.DrawImage(mb, dx, dy, (float)m.Dip.Width, (float)m.Dip.Height);
            }
        }

        // 像素缓存
        var data = _bmp.LockBits(new SD.Rectangle(0, 0, _bw, _bh), SD.Imaging.ImageLockMode.ReadOnly, SD.Imaging.PixelFormat.Format32bppArgb);
        _stride = data.Stride;
        _px = new byte[Math.Abs(data.Stride) * _bh];
        Marshal.Copy(data.Scan0, _px, 0, _px.Length);
        _bmp.UnlockBits(data);
        DebugLog.Write("[SNIP] pixel grid ready");
    }

    private static BitmapSource ToSource(SD.Bitmap bmp)
    {
        IntPtr h = bmp.GetHbitmap();
        try
        {
            var s = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            s.Freeze(); return s;
        }
        finally { DeleteObject(h); }
    }

    // ==================== 坐标换算 ====================
    /// <summary>窗口局部 DIP → 物理像素（该窗口渲染 DPI = Phys/实际窗口尺寸，布局后取 ActualWidth）。</summary>
    private static Point LocalToPhys(MonWindow m, Point local)
    {
        double w = m.Win.ActualWidth > 1 ? m.Win.ActualWidth : m.Dip.Width;
        double h = m.Win.ActualHeight > 1 ? m.Win.ActualHeight : m.Dip.Height;
        double sx = m.Phys.Width / w, sy = m.Phys.Height / h;
        return new Point(local.X * sx, local.Y * sy);
    }
    /// <summary>物理像素差量 → 窗口局部 DIP。</summary>
    private static Point PhysDeltaToLocal(MonWindow m, double dx, double dy)
    {
        double w = m.Win.ActualWidth > 1 ? m.Win.ActualWidth : m.Dip.Width;
        double h = m.Win.ActualHeight > 1 ? m.Win.ActualHeight : m.Dip.Height;
        return new Point(dx * w / m.Phys.Width, dy * h / m.Phys.Height);
    }
    /// <summary>窗口局部 DIP → 虚拟屏全局 DIP（经物理像素，绝对一致）。</summary>
    private static Point ToVirtual(MonWindow m, Point local)
    {
        var p = LocalToPhys(m, local);
        return new Point(p.X / m.MainDpi, p.Y / m.MainDpi);
    }

    private Color GetPixelAt(double vx, double vy)
    {
        if (_px == null) return Colors.Transparent;
        int x = (int)Math.Round(vx - _vsX), y = (int)Math.Round(vy - _vsY);
        x = Math.Clamp(x, 0, _bw - 1); y = Math.Clamp(y, 0, _bh - 1);
        int i = y * _stride + x * 4;
        return Color.FromArgb(_px[i + 3], _px[i + 2], _px[i + 1], _px[i]);
    }

    // ==================== 初始化可视元素（画在鼠标所在屏） ====================
    private void InitVisuals()
    {
        var maskBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
        for (int i = 0; i < 4; i++) { _masks[i] = new Rectangle { Fill = maskBrush, IsHitTestVisible = false }; }
        _border.Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xFF)); _border.StrokeThickness = 1.4; _border.IsHitTestVisible = false;
        _sizeLabel.Foreground = Brushes.White; _sizeLabel.FontSize = 11.5; _sizeLabel.IsHitTestVisible = false;
        _sizeBg = new Border { Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1E)), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Child = _sizeLabel };
        for (int i = 0; i < 8; i++)
        {
            _handles[i] = new Rectangle { Width = 9, Height = 9, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), StrokeThickness = 1.2, Cursor = Cursors.SizeAll };
            int idx = i;
            _handles[i].PreviewMouseLeftButtonDown += (s, e) => { _dragKind = idx + 2; _dragStartPt = e.GetPosition(_uiHost!.Win); _dragStartSel = _sel; _uiHost.Win.CaptureMouse(); e.Handled = true; };
        }
        // 放大镜
        for (int i = 0; i < LoupeN * LoupeN; i++)
        {
            _loupeBrushes[i] = new SolidColorBrush(Colors.Black);
            _loupeCells[i] = new Rectangle { Width = CellPx, Height = CellPx, Fill = _loupeBrushes[i], Stroke = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00)), StrokeThickness = 0.5 };
            _loupe.Children.Add(_loupeCells[i]);
        }
        _loupeCenter.Width = CellPx; _loupeCenter.Height = CellPx;
        _loupeCenter.Stroke = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)); _loupeCenter.StrokeThickness = 1.6; _loupeCenter.Fill = Brushes.Transparent;
        _loupe.Children.Add(_loupeCenter);
        _loupe.Width = LoupeN * CellPx; _loupe.Height = LoupeN * CellPx;
        _loupeText.Foreground = Brushes.White; _loupeText.FontSize = 11; _loupeText.TextAlignment = TextAlignment.Center;
        _loupePanel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8),
            Child = _loupeText
        };
    }

    // ==================== 鼠标交互（任一屏窗口触发） ====================
    private MonWindow? HostOf(object sender)
    {
        var w = Window.GetWindow((DependencyObject)sender);
        return _mw.FirstOrDefault(m => ReferenceEquals(m.Win, w));
    }

    /// <summary>事件源是否在工具条内（工具条有自己的 Click 逻辑，画布不得拦截）。</summary>
    private bool FromToolbar(object source)
    {
        var d = source as DependencyObject;
        while (d != null)
        {
            if (ReferenceEquals(d, _toolbar)) { DebugLog.Write("[SNIP] from-toolbar hit"); return true; }
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void OnMouseLeftDown(object s, MouseButtonEventArgs e)
    {
        // 工具条内部：完全放行（不设 Handled，让按钮的 MouseDown/Up→Click 正常工作）
        if (FromToolbar(e.OriginalSource)) return;
        var host = HostOf(s); if (host == null) return;
        var vp = ToVirtual(host, e.GetPosition(host.Win));
        _uiHost = host;
        if (_hasSel && _sel.Contains(vp))
        {
            _dragKind = 1; _dragStartPt = e.GetPosition(host.Win); _dragStartSel = _sel;
            host.Win.CaptureMouse();
        }
        else
        {
            _drawing = true; _drawStart = vp;
            _sel = new Rect(vp, vp); _hasSel = true;
            if (_toolbar != null && host.Canvas.Children.Contains(_toolbar)) host.Canvas.Children.Remove(_toolbar);
            host.Win.CaptureMouse();
        }
        RebuildUi(host);
        e.Handled = true;
    }

    private void OnMouseMove(object s, MouseEventArgs e)
    {
        if (FromToolbar(e.OriginalSource)) return;
        var host = HostOf(s); if (host == null) return;
        var vp = ToVirtual(host, e.GetPosition(host.Win));
        _uiHost = host;
        if (_drawing)
        {
            _sel = new Rect(_drawStart, vp);
            // 转成 host 局部坐标画
            RebuildUi(host);
        }
        else if (_dragKind > 0)
        {
            // 拖拽差量：局部DIP → 物理 → 全局DIP（跨屏一致）
            var p = e.GetPosition(host.Win);
            var cur = LocalToPhys(host, p);
            var start = LocalToPhys(host, _dragStartPt);
            var dx = (cur.X - start.X) / host.MainDpi;
            var dy = (cur.Y - start.Y) / host.MainDpi;
            if (_dragKind == 1) _sel = new Rect(_dragStartSel.X + dx, _dragStartSel.Y + dy, _dragStartSel.Width, _dragStartSel.Height);
            else ApplyHandleDrag(dx, dy);
            _sel = ClampSel(_sel);
            RebuildUi(host);
        }
        else
        {
            UpdateLoupeOnly(host, vp);
        }
        e.Handled = true;
    }

    private void ApplyHandleDrag(double dx, double dy)
    {
        double l = _dragStartSel.X, t = _dragStartSel.Y, r = _dragStartSel.Right, b = _dragStartSel.Bottom;
        switch (_dragKind)
        {
            case 2: l += dx; t += dy; break;
            case 3: t += dy; break;
            case 4: r += dx; t += dy; break;
            case 5: r += dx; break;
            case 6: r += dx; b += dy; break;
            case 7: b += dy; break;
            case 8: l += dx; b += dy; break;
            case 9: l += dx; break;
        }
        _sel = Rect.Intersect(new Rect(Math.Min(l, r), Math.Min(t, b), Math.Abs(r - l), Math.Abs(b - t)), new Rect(_vsX, _vsY, _winW, _winH));
    }

    private Rect ClampSel(Rect r)
    {
        double x = Math.Clamp(r.X, _vsX, _vsX + _winW), y = Math.Clamp(r.Y, _vsY, _vsY + _winH);
        double w = Math.Min(r.Width, _vsX + _winW - x), h = Math.Min(r.Height, _vsY + _winH - y);
        return new Rect(x, y, Math.Max(0, w), Math.Max(0, h));
    }

    private void OnMouseLeftUp(object s, MouseButtonEventArgs e)
    {
        if (FromToolbar(e.OriginalSource)) return;  // 按钮抬起交给按钮自己（Click 需要）
        var host = HostOf(s); if (host == null) return;
        bool was = _drawing || _dragKind > 0;
        _drawing = false; _dragKind = 0;
        host.Win.ReleaseMouseCapture();
        if (was && _sel.Width > 6 && _sel.Height > 6) ShowToolbar(host);
        else if (_sel.Width <= 6 || _sel.Height <= 6) { _hasSel = false; }
        RebuildUi(host);
        e.Handled = true;
    }

    private void OnWindowKeyDown(object s, KeyEventArgs e)
    {
        var host = _uiHost ?? _mw.FirstOrDefault();
        if (e.Key == Key.Escape) { CloseAll(); e.Handled = true; return; }
        if (e.Key == Key.Enter) { if (_hasSel && _sel.Width > 4) CopySelection(); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.S) { if (_hasSel && _sel.Width > 4) SaveSelection(); e.Handled = true; return; }
        if (_hasSel && (e.Key is Key.Left or Key.Right or Key.Up or Key.Down))
        {
            double d = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 10 : 1;
            switch (e.Key)
            {
                case Key.Left: _sel.X -= d; break;
                case Key.Right: _sel.X += d; break;
                case Key.Up: _sel.Y -= d; break;
                case Key.Down: _sel.Y += d; break;
            }
            _sel = ClampSel(_sel);
            if (host != null) RebuildUi(host);
            e.Handled = true;
        }
    }

    // ==================== UI 重建（每次交互全量重画到当前屏） ====================
    /// <summary>全局虚拟 DIP → 窗口局部 DIP（经物理像素，跨屏一致）。</summary>
    private static Rect ToLocal(MonWindow m, Rect r)
    {
        // 全局DIP → 物理 → 局部DIP
        double px = r.X * m.MainDpi, py = r.Y * m.MainDpi, pw = r.Width * m.MainDpi, ph = r.Height * m.MainDpi;
        double w = m.Win.ActualWidth > 1 ? m.Win.ActualWidth : m.Dip.Width;
        double h = m.Win.ActualHeight > 1 ? m.Win.ActualHeight : m.Dip.Height;
        double sx = w / m.Phys.Width, sy = h / m.Phys.Height;
        // 物理窗口原点 → 局部
        double lx = (px - m.Phys.X) * sx, ly = (py - m.Phys.Y) * sy;
        return new Rect(lx, ly, pw * sx, ph * sy);
    }

    private void RebuildUi(MonWindow host)
    {
        var c = host.Canvas;
        c.Children.Clear();
        // 工具条在选区完成后持久存在（Clear 后重新挂回，否则松手即被清掉）
        if (_toolbar != null && _hasSel && _sel.Width > 6)
        {
            // 重新定位（RebuildUi 可能因为拖拽调整改变了选区）
            if (_toolbar.Parent is Panel pp) pp.Children.Remove(_toolbar);
            c.Children.Add(_toolbar);
            double hostW = host.Win.ActualWidth > 1 ? host.Win.ActualWidth : host.Dip.Width;
            double hostH = host.Win.ActualHeight > 1 ? host.Win.ActualHeight : host.Dip.Height;
            _toolbar.Measure(new Size(hostW, hostH));
            double tw = _toolbar.DesiredSize.Width, th = _toolbar.DesiredSize.Height;
            var localSel = ToLocal(host, _sel);
            double tx = localSel.Right - tw, ty = localSel.Bottom + 8;
            if (ty + th > hostH - 8) ty = localSel.Bottom - th - 8;
            if (tx < 8) tx = 8;
            Canvas.SetLeft(_toolbar, tx); Canvas.SetTop(_toolbar, ty);
        }
        Func<Rect, Rect> L = r => ToLocal(host, r);
        var sel = _hasSel ? _sel : Rect.Empty;
        double hw = host.Win.ActualWidth > 1 ? host.Win.ActualWidth : host.Dip.Width;
        double hh = host.Win.ActualHeight > 1 ? host.Win.ActualHeight : host.Dip.Height;
        if (_hasSel)
        {
            var l = L(sel);
            // 遮罩 4 块（用实际窗口尺寸，跨屏正确）
            AddRect(c, 0, 0, hw, Math.Max(0, l.Y), maskBrush: true);
            AddRect(c, 0, l.Bottom, hw, Math.Max(0, hh - l.Bottom), maskBrush: true);
            AddRect(c, 0, l.Y, Math.Max(0, l.X), l.Height, maskBrush: true);
            AddRect(c, l.Right, l.Y, Math.Max(0, hw - l.Right), l.Height, maskBrush: true);
            // 边框
            var b = new Rectangle { Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xFF)), StrokeThickness = 1.4 };
            SetR(b, l); c.Children.Add(b);
            // 控制点
            if (sel.Width > 2 && sel.Height > 2)
            {
                var hx = new[] { l.X, l.X + l.Width / 2, l.Right, l.Right, l.Right, l.X + l.Width / 2, l.X, l.X };
                var hy = new[] { l.Y, l.Y, l.Y, l.Y + l.Height / 2, l.Bottom, l.Bottom, l.Bottom, l.Y + l.Height / 2 };
                for (int i = 0; i < 8; i++)
                {
                    var h = _handles[i];
                    Canvas.SetLeft(h, hx[i] - 4.5); Canvas.SetTop(h, hy[i] - 4.5);
                    if (h.Parent is Panel p) p.Children.Remove(h);
                    c.Children.Add(h);
                }
            }
            // 尺寸
            if (sel.Width > 4)
            {
                _sizeLabel.Text = $"{(int)Math.Round(sel.Width)} × {(int)Math.Round(sel.Height)}";
                var bg = _sizeBg!;
                var lbl = L(sel);
                double lx = lbl.X, ly = lbl.Y - 30;
                if (ly < 4) ly = lbl.Bottom + 6;
                Canvas.SetLeft(bg, lx); Canvas.SetTop(bg, ly);
                if (bg.Parent is Panel p) p.Children.Remove(bg);
                c.Children.Add(bg);
            }
        }
        // 放大镜
        var mp = Mouse.GetPosition(host.Win);
        var vpNow = ToVirtual(host, mp);
        DrawLoupe(c, vpNow, L);
    }

    private void UpdateLoupeOnly(MonWindow host, Point vp)
    {
        // 未拖拽时只动放大镜（性能：不重画整个选区）
        var c = host.Canvas;
        // 找到已有 loupe 元素位置更新即可；简单起见仍全量（元素少）
        RebuildUi(host);
    }

    private void AddRect(Canvas c, double x, double y, double w, double h, bool maskBrush)
    {
        if (w <= 0.5 || h <= 0.5) return;
        var r = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00)), IsHitTestVisible = false };
        SetR(r, new Rect(x, y, w, h)); c.Children.Add(r);
    }

    private static void SetR(Rectangle r, Rect rc) { Canvas.SetLeft(r, rc.X); Canvas.SetTop(r, rc.Y); r.Width = rc.Width; r.Height = rc.Height; }

    private void DrawLoupe(Canvas c, Point vp, Func<Rect, Rect> toLocal)
    {
        var host = _uiHost!; if (host == null) return;
        double hostW = host.Win.ActualWidth > 1 ? host.Win.ActualWidth : host.Dip.Width;
        double hostH = host.Win.ActualHeight > 1 ? host.Win.ActualHeight : host.Dip.Height;
        var col = GetPixelAt(vp.X, vp.Y);
        int half = LoupeN / 2;
        for (int gy = 0; gy < LoupeN; gy++)
        for (int gx = 0; gx < LoupeN; gx++)
        {
            int i = gy * LoupeN + gx;
            _loupeBrushes[i].Color = GetPixelAt(vp.X + (gx - half), vp.Y + (gy - half));
        }
        Canvas.SetLeft(_loupeCenter, half * CellPx); Canvas.SetTop(_loupeCenter, half * CellPx);
        var txt = $"#{col.R:X2}{col.G:X2}{col.B:X2}  RGB({col.R},{col.G},{col.B})\n({(int)Math.Round(vp.X)}, {(int)Math.Round(vp.Y)})";
        if (txt != _lastLoupeText) { _loupeText.Text = txt; _lastLoupeText = txt; }
        _loupePanel!.Measure(new Size(hostW, hostH));
        double lw = Math.Max(120, _loupePanel.DesiredSize.Width), lh = Math.Max(40, _loupePanel.DesiredSize.Height);
        var local = toLocal(new Rect(vp.X, vp.Y, 0, 0));
        double lx = Math.Round(local.X + 24), ly = Math.Round(local.Y + 24);
        double panelSize = LoupeN * CellPx;
        if (lx + lw > hostW - 8) lx = Math.Round(local.X - lw - 24);
        if (ly + lh + panelSize + 20 > hostH - 8) ly = Math.Round(local.Y - lh - panelSize - 32);
        if (lx < 8) lx = 8; if (ly < 8) ly = 8;
        Canvas.SetLeft(_loupe, lx + 8); Canvas.SetTop(_loupe, ly + 8);
        Canvas.SetLeft(_loupePanel, lx); Canvas.SetTop(_loupePanel, ly + panelSize + 12);
        if (_loupe.Parent is Panel p) p.Children.Remove(_loupe);
        if (_loupePanel.Parent is Panel p2) p2.Children.Remove(_loupePanel);
        c.Children.Add(_loupe); c.Children.Add(_loupePanel);
    }

    // ==================== 操作条（Snipaste 式图标工具条） ====================
    private void ShowToolbar(MonWindow host)
    {
        if (_toolbar == null)
        {
            _toolbar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x14, 0x14, 0x16)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x4A, 0x90, 0xFF)),
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(6, 5, 6, 5)
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(MkToolBtn("复制", "复制到剪贴板 (Enter)", CopySelection, Color.FromRgb(0x4A, 0x90, 0xFF)));
            sp.Children.Add(MkSep());
            sp.Children.Add(MkToolBtn("保存", "保存为 PNG (Ctrl+S)", SaveSelection, Color.FromRgb(0x30, 0xD1, 0x58)));
            sp.Children.Add(MkSep());
            sp.Children.Add(MkToolBtn("贴图", "贴图置顶悬浮（滚轮缩放）", PinSelection, Color.FromRgb(0xFF, 0x9F, 0x0A)));
            _toolbar.Child = sp;
        }
        if (_toolbar.Parent is Panel p) p.Children.Remove(_toolbar);
        host.Canvas.Children.Add(_toolbar);
        double hostW = host.Win.ActualWidth > 1 ? host.Win.ActualWidth : host.Dip.Width;
        double hostH = host.Win.ActualHeight > 1 ? host.Win.ActualHeight : host.Dip.Height;
        _toolbar.Measure(new Size(hostW, hostH));
        double tw = _toolbar.DesiredSize.Width, th = _toolbar.DesiredSize.Height;
        var localSel = ToLocal(host, _sel);
        double x = localSel.Right - tw, y = localSel.Bottom + 8;
        if (y + th > hostH - 8) y = localSel.Bottom - th - 8;
        if (x < 8) x = 8;
        Canvas.SetLeft(_toolbar, x); Canvas.SetTop(_toolbar, y);
    }

    /// <summary>图标 + 文字 的双信息按钮（一眼看懂，不用猜）。</summary>
    private Button MkToolBtn(string label, string tip, Action act, Color accent)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        // 图标点（彩色圆点，像 Snipaste 的彩色按钮）
        sp.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(accent), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        sp.Children.Add(new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center });

        var b = new Button
        {
            Content = sp,
            ToolTip = tip,
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 7, 12, 7),
            Cursor = Cursors.Hand,
            Focusable = false
        };
        b.Click += (s, e) => { DebugLog.Write("[SNIP] toolbtn click: " + label); act(); e.Handled = true; };
        b.PreviewMouseLeftButtonUp += (s, e) => { if (b.IsMouseOver) { DebugLog.Write("[SNIP] toolbtn previewup: " + label); act(); e.Handled = true; } };
        return b;
    }

    private static System.Windows.Shapes.Path MkSep()
    {
        var sep = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0 0 L 0 18"),
            Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1,
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        return sep;
    }

    // ==================== 输出 ====================
    private BitmapSource? RenderSelection()
    {
        if (_bmp == null || !_hasSel) return null;
        int x = (int)Math.Round(_sel.X - _vsX), y = (int)Math.Round(_sel.Y - _vsY);
        int w = (int)Math.Round(_sel.Width), h = (int)Math.Round(_sel.Height);
        x = Math.Clamp(x, 0, _bw - 1); y = Math.Clamp(y, 0, _bh - 1);
        w = Math.Clamp(w, 1, _bw - x); h = Math.Clamp(h, 1, _bh - y);
        using var part = _bmp.Clone(new SD.Rectangle(x, y, w, h), _bmp.PixelFormat);
        return ToSource(part);
    }

    private void CopySelection()
    {
        var src = RenderSelection(); if (src == null) return;
        try { Clipboard.SetImage(src); CloseAll(); } catch { }
    }

    private void SaveSelection()
    {
        var src = RenderSelection(); if (src == null) return;
        // 注意：this 是 0x0 隐藏宿主，不能作 Owner——用当前活动的屏覆盖窗口
        var owner = _uiHost?.Win ?? _mw.FirstOrDefault()?.Win;
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图片 (*.png)|*.png", Title = "保存截图", FileName = "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") };
        bool? ok;
        if (owner != null) ok = dlg.ShowDialog(owner);
        else ok = dlg.ShowDialog();
        if (ok != true) return;
        try { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(src)); using var fs = File.Create(dlg.FileName); enc.Save(fs); CloseAll(); }
        catch (Exception ex) { MessageBox.Show(owner, "保存失败：" + ex.Message, "错误"); }
    }

    private void PinSelection()
    {
        var src = RenderSelection(); if (src == null) return;
        double x = _sel.X, y = _sel.Y;
        var sel = _sel;
        CloseAll();
        var pin = new PinWindow(src, x, y, sel.Width, sel.Height);
        pin.Show();
    }

    /// <summary>结束截图：每次新建模式 → 真正关闭一切（状态彻底清零，下次 F1 全新）。</summary>
    private void CloseAll()
    {
        StopAndClose();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 每次新建模式：允许真关闭（不再拦截）
        base.OnClosing(e);
    }

    private bool _disposing;

    /// <summary>App 退出时真正销毁（内部用）。</summary>
    public void Shutdown()
    {
        _disposing = true;
        foreach (var m in _mw) { try { m.Win.Close(); } catch { } }
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var m in _mw) { try { if (m.Win.IsLoaded) m.Win.Close(); } catch { } }
        if (ReferenceEquals(_pooled, this)) _pooled = null;
        _bmp?.Dispose(); _bmp = null; _px = null;
        base.OnClosed(e);
    }
}