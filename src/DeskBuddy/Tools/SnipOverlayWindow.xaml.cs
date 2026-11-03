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

    /// <summary>新建实例激活：先截屏 → 再建窗显示（窗口创建时会先显示黑底——必须已截好图再建窗，否则截到自己=全黑）。</summary>
    private void BuildAllAndShow()
    {
        _active = true;
        try
        {
            // 阶段1：先截屏（含布局枚举），纯内存
            EnumPhys();
            _mw.Clear(); _shots.Clear(); _hwnds.Clear();
            var prepared = new List<(SD.Rectangle Phys, Rect Dip, Size WinDip, System.Windows.Media.Imaging.BitmapSource Src, SD.Bitmap Bmp)>();
            for (int i = 0; i < _physRects.Count; i++)
            {
                var phys = _physRects[i];
                var bmp = new SD.Bitmap(phys.Width, phys.Height);
                using (var g = SD.Graphics.FromImage(bmp))
                    g.CopyFromScreen(phys.X, phys.Y, 0, 0, new SD.Size(phys.Width, phys.Height));
                var dip = new Rect(phys.X / _mainDpi, phys.Y / _mainDpi, phys.Width / _mainDpi, phys.Height / _mainDpi);
                // 窗口尺寸用该屏自己的缩放，混合 DPI 下每屏都能铺满（100% 时与 dip 相同）
                var sc = ScaleOf(i);
                prepared.Add((phys, dip, new Size(phys.Width / sc, phys.Height / sc), ToSource(bmp), bmp));
            }
            DebugLog.Write($"[SNIP] captured {prepared.Count} screens (pre-window)");
            // 阶段2：建窗 + 立即挂图 + 显示（全新窗口状态机可靠）
            foreach (var (phys, dip, winDip, src, bmp) in prepared)
            {
                var win = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    ResizeMode = ResizeMode.NoResize,
                    Cursor = Cursors.Cross,
                    Background = Brushes.Black,
                    // 关键：WPF 布局尺寸显式给（否则 ActualWidth=0，Image 渲染 0×0 → 全黑）
                    Left = dip.X, Top = dip.Y,
                    Width = winDip.Width, Height = winDip.Height,
                };
                var img = new Image { Source = src, Stretch = Stretch.Fill };   // 建窗即带图
                var canvas = new Canvas();
                var grid = new Grid();
                grid.Children.Add(img); grid.Children.Add(canvas);
                win.Content = grid;
                win.PreviewMouseLeftButtonDown += OnMouseLeftDown;
                win.PreviewMouseMove += OnMouseMove;
                win.PreviewMouseLeftButtonUp += OnMouseLeftUp;
                win.PreviewKeyDown += OnWindowKeyDown;
                var ph = phys;
                win.SourceInitialized += (s2, e2) =>
                {
                    var h = new WindowInteropHelper(win).Handle;
                    _hwnds.Add(h);
                    PinGeometry(win, h, ph);
                };
                win.Show();
                // Show 之后 WPF 可能按自己的 DPI 重新排一次布局/尺寸（混合 DPI 下会把窗口放大 1.5 倍，
                // 冻结图被拉伸 = 用户看到“竖屏分辨率变了”）——这里在消息循环空闲后再钉一次物理像素。
                win.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    try
                    {
                        var h = new WindowInteropHelper(win).Handle;
                        if (h != IntPtr.Zero) PinGeometry(win, h, ph);
                    }
                    catch { }
                }));
                var mw = new MonWindow { Win = win, Img = img, Canvas = canvas, Dip = dip, MainDpi = _mainDpi, Phys = phys };
                _mw.Add(mw);
                _shots.Add((mw, bmp));
            }
            // 统一虚拟 DIP 视界
            var allX = _mw.Select(m => m.Dip.X).DefaultIfEmpty(0).Min();
            var allY = _mw.Select(m => m.Dip.Y).DefaultIfEmpty(0).Min();
            _vsX = allX; _vsY = allY;
            _winW = _mw.Select(m => m.Dip.Right).DefaultIfEmpty(0).Max() - allX;
            _winH = _mw.Select(m => m.Dip.Bottom).DefaultIfEmpty(0).Max() - allY;
            foreach (var m in _mw) m.Win.Activate();
            // 重活后置（取色网格 + 像素缓存）
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try { BuildPixelGrid(); } catch { }
            }));
        }
        catch (Exception ex) { DebugLog.Write($"[SNIP] BuildAllAndShow ex: {ex.Message}"); _active = false; }
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
        var act = (Action<RECT, IntPtr>)GCHandle.FromIntPtr(data).Target!;
        act(r, hMon);
        return true;
    }

    // ==================== 截屏（预创建窗口 + 激活换图） ====================
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmon, int dpiType, out uint dpiX, out uint dpiY);
    private const int SW_HIDE = 0, SW_SHOW = 5, SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_HIDEWINDOW = 0x0080;
    private readonly List<IntPtr> _hwnds = new();

    private readonly List<(MonWindow MW, SD.Bitmap Bmp)> _shots = new();
    private List<SD.Rectangle> _physRects = new();
    private List<double> _monScales = new();          // 每个屏的 effective 缩放（1.0 = 100%）
    private double _mainDpi = 1.0;                    // 全局 DIP 基准 = 主屏缩放

    /// <summary>取某显示器的 effective DPI（Win8.1+；拿不到按 96 处理）。</summary>
    private static uint DpiOf(IntPtr hmon)
    {
        try
        {
            if (GetDpiForMonitor(hmon, 0 /*MDT_EFFECTIVE_DPI*/, out var dx, out _) == 0 && dx > 0) return dx;
        }
        catch { }
        return 96;
    }

    /// <summary>该屏自己的缩放比（与 _physRects 同下标）。</summary>
    private double ScaleOf(int i) => (i >= 0 && i < _monScales.Count && _monScales[i] > 0.1) ? _monScales[i] : _mainDpi;

    /// <summary>
    /// 把覆盖窗口钉成「该屏的物理像素矩形」。
    /// 必须用物理像素而不是 WPF 的 DIP 尺寸：窗口创建时的 DPI 与落屏后的 DPI 不一致时，
    /// WPF 会把 1080 DIP 建成 1620 px（1.5 倍），冻结图被拉伸 —— 这就是“竖屏分辨率变了”的来源。
    /// 尺寸改由 Win32 钉死，WPF 的 DPI 只影响内部 DIP 换算（会互相抵消，仍是 1:1）。
    /// </summary>
    private void PinGeometry(Window win, IntPtr h, SD.Rectangle ph)
    {
        SetWindowPos(h, new IntPtr(-1) /*HWND_TOPMOST*/, ph.X, ph.Y, ph.Width, ph.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
        if (HwndSource.FromHwnd(h) is not HwndSource hs) return;
        hs.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            // WPF 收到 WM_DPICHANGED 会按“建议矩形”Resize，跨屏时又把窗口改大/改小 → 延后压回去。
            // 不拦这个消息（要让 WPF 更新自己的 DPI，绘制比例才对），只做收尾。
            if (msg == 0x02E0 /*WM_DPICHANGED*/)
            {
                win.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    try { SetWindowPos(hwnd, new IntPtr(-1), ph.X, ph.Y, ph.Width, ph.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW); }
                    catch { }
                }));
            }
            return IntPtr.Zero;
        });
    }

    /// <summary>枚举物理屏 + 每屏真实缩放（预创建时算好，激活时直接复用）。</summary>
    private void EnumPhys()
    {
        _physRects = new List<SD.Rectangle>();
        _monScales = new List<double>();
        var act = (Action<RECT, IntPtr>)((r, hmon) =>
        {
            _physRects.Add(new SD.Rectangle(r.L, r.T, r.R - r.L, r.B - r.T));
            _monScales.Add(DpiOf(hmon) / 96.0);
        });
        var gc = GCHandle.Alloc(act);
        try { EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, CollectPhys, GCHandle.ToIntPtr(gc)); }
        finally { gc.Free(); }

        // 全局 DIP 基准取主屏的 **effective DPI**。
        // ⚠ 不能用 SystemParameters.PrimaryScreenWidth：它按「系统 DPI」换算，
        // 用户改过缩放后注册表 AppliedDPI 会留下旧值，与实际显示器 DPI 不一致，
        // 会让每屏覆盖窗口尺寸和裁剪区域被放大 1.5 倍 —— 竖屏因此框不到、截不出。
        var mainIdx = _physRects.FindIndex(r => r.X == 0 && r.Y == 0);
        if (mainIdx < 0 && _physRects.Count > 0) mainIdx = 0;
        _mainDpi = ScaleOf(mainIdx);
        DebugLog.Write($"[SNIP] monitors: {string.Join(" | ", _physRects.Select((r, i) => $"{r.X},{r.Y} {r.Width}x{r.Height}@{ScaleOf(i):0.##}x"))} mainDpi={_mainDpi:0.##}");
    }

    /// <summary>预创建：只建窗口骨架（每屏子窗口 + 事件），不截屏。
    /// 显示管理全走 Win32（WPF 的 Visibility/Show/Hide 互相打架，是 F1 失效的根因）。</summary>
    private void BuildMonitorWindows(bool capture)
    {
        EnumPhys();   // 无论 capture 与否都先枚举（否则首激活误判布局变更）
        _mw.Clear(); _shots.Clear(); _hwnds.Clear();
        int wi = 0;
        for (int i = 0; i < _physRects.Count; i++)
        {
            var phys = _physRects[i];
            // 全局 DIP 是统一基准；窗口自身尺寸用它自己的缩放，混合 DPI 下每屏才铺满
            var dip = new Rect(phys.X / _mainDpi, phys.Y / _mainDpi, phys.Width / _mainDpi, phys.Height / _mainDpi);
            var sc = ScaleOf(i);
            var win = new Window
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                ResizeMode = ResizeMode.NoResize,
                Cursor = Cursors.Cross,
                Background = Brushes.Black,
                Left = dip.X, Top = dip.Y,
                Width = phys.Width / sc, Height = phys.Height / sc,
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
                _hwnds.Add(h);
                // 物理像素钉死位置与尺寸（同 BuildAllAndShow 的说明）
                SetWindowPos(h, new IntPtr(-1) /*HWND_TOPMOST*/, phys.X, phys.Y, phys.Width, phys.Height,
                    SWP_NOACTIVATE | (isCapture ? SWP_SHOWWINDOW : 0));
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
    /// <summary>窗口局部 DIP → 虚拟屏全局 DIP（经物理像素，绝对一致）。
    /// ⚠必须叠加该屏的物理原点，否则副屏（如左侧竖屏 x=-1080）的点击会被当成主屏坐标。</summary>
    private static Point ToVirtual(MonWindow m, Point local)
    {
        var p = LocalToPhys(m, local);
        return new Point((p.X + m.Phys.X) / m.MainDpi, (p.Y + m.Phys.Y) / m.MainDpi);
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
            // 尺寸标注给「导出后的像素尺寸」（全局 DIP × _mainDpi = 物理像素），
            // 而不是 DIP —— 混合 DPI 下 DIP 数字和实际截图大小对不上，容易让人以为框错了
            if (sel.Width > 4)
            {
                _sizeLabel.Text = $"{(int)Math.Round(sel.Width * _mainDpi)} × {(int)Math.Round(sel.Height * _mainDpi)}";
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
            // 截图条只做「复制 / 保存 / 贴图」；箭头、文字等标注在贴图窗口里做
            sp.Children.Add(MkIconBtn("copy", "复制到剪贴板 (Enter)", CopySelection, Color.FromRgb(0x4A, 0x90, 0xFF)));
            sp.Children.Add(MkIconBtn("save", "保存为 PNG (Ctrl+S)", SaveSelection, Color.FromRgb(0x30, 0xD1, 0x58)));
            sp.Children.Add(MkIconBtn("pin", "贴图置顶悬浮（可在贴图上继续标注）", PinSelection, Color.FromRgb(0xFF, 0x9F, 0x0A)));
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

    /// <summary>20×20 坐标系里的纯矢量图标（只用描边，不依赖任何字体/中文）。</summary>
    private static string IconData(string kind) => kind switch
    {
        "copy" => "M 7,3 H 16 V 12 M 3,8 H 12 V 17 H 3 Z",                                         // 两张纸：复制
        "save" => "M 3,3 H 13.5 L 17,6.5 V 17 H 3 Z M 7,3 V 8 H 13 V 3 M 6.5,17 V 12 H 13.5 V 17", // 软盘：保存
        "pin" => "M 6.5,4 H 13.5 M 8,4 L 7.2,11 H 12.8 L 12,4 M 10,11 V 17",                       // 图钉：贴图
        _ => "M 3,3 H 17 V 17 H 3 Z"
    };

    private static System.Windows.Shapes.Path MkIcon(string kind, Color color) => new()
    {
        Data = Geometry.Parse(IconData(kind)),
        Stroke = new SolidColorBrush(color),
        StrokeThickness = 1.7,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Fill = Brushes.Transparent,
        Width = 20, Height = 20,
        Stretch = Stretch.None,
        IsHitTestVisible = false
    };

    /// <summary>操作条图标按钮：按下并在按钮内抬起算一次
    /// （不绑 Click，避免 Click + PreviewMouseUp 双触发）。</summary>
    private Button MkIconBtn(string kind, string tip, Action act, Color accent)
    {
        var icon = MkIcon(kind, accent);
        var b = new Button
        {
            Content = icon,
            Tag = icon,
            ToolTip = tip,
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(0),
            Width = 34, Height = 32,
            Cursor = Cursors.Hand,
            Focusable = false
        };
        var downInside = false;
        b.PreviewMouseLeftButtonDown += (s, e) => { downInside = true; e.Handled = true; };   // 别让画布抢去起选区
        b.PreviewMouseLeftButtonUp += (s, e) =>
        {
            if (downInside)
            {
                downInside = false;
                DebugLog.Write($"[SNIP] icon-btn: {kind}");
                act();
            }
            e.Handled = true;
        };
        return b;
    }

    // ==================== 输出（原始物理像素，零重采样 = 不糊） ====================
    /// <summary>从【原始物理像素截图】裁剪选区：选区(全局DIP) → 所在屏 → 该屏物理坐标。
    /// 不走 DIP 网格（那会先缩到 2560 再拉伸 = 糊）。</summary>
    private BitmapSource? RenderSelection()
    {
        if (!_hasSel || _shots.Count == 0) return null;
        // 选区（全局 DIP）转物理像素
        double px = _sel.X * _mainDpi, py = _sel.Y * _mainDpi;
        double pw = _sel.Width * _mainDpi, ph = _sel.Height * _mainDpi;
        // 找选区主要落在哪个屏（按选区中心）
        double cx = px + pw / 2, cy = py + ph / 2;
        MonWindow? best = null;
        foreach (var m in _mw)
        {
            // m.Phys 是物理坐标矩形
            if (cx >= m.Phys.X && cx < m.Phys.X + m.Phys.Width && cy >= m.Phys.Y && cy < m.Phys.Y + m.Phys.Height)
            { best = m; break; }
        }
        best ??= _mw.FirstOrDefault(m => _sel.IntersectsWith(new Rect(m.Phys.X / _mainDpi, m.Phys.Y / _mainDpi, m.Phys.Width / _mainDpi, m.Phys.Height / _mainDpi)));
        if (best == null) return null;
        // 找对应截图（同屏）
        var shot = _shots.FirstOrDefault(s => ReferenceEquals(s.MW, best));
        var bmp = shot.Bmp ?? _bmp;
        if (bmp == null) return null;
        // 全局物理 → 该屏物理局部坐标
        int x = (int)Math.Round(px - best.Phys.X), y = (int)Math.Round(py - best.Phys.Y);
        int w = Math.Max(1, (int)Math.Round(pw)), h = Math.Max(1, (int)Math.Round(ph));
        x = Math.Clamp(x, 0, bmp.Width - 1); y = Math.Clamp(y, 0, bmp.Height - 1);
        w = Math.Clamp(w, 1, bmp.Width - x); h = Math.Clamp(h, 1, bmp.Height - y);
        try
        {
            using var part = bmp.Clone(new SD.Rectangle(x, y, w, h), bmp.PixelFormat);
            return ToSource(part);
        }
        catch { return null; }
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
        // 贴图显示尺寸 = 选区 DIP（视觉与框选时一致）；图片本体是物理像素高清
        DebugLog.Write($"[SNIP] pin: src={src.PixelWidth}x{src.PixelHeight} sel={sel.Width:F0}x{sel.Height:F0} at ({sel.X:F0},{sel.Y:F0})");
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
