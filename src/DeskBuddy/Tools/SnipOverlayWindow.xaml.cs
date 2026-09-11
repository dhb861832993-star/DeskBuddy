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
        public required Image Img;          // 冻结层（该屏物理像素，1:1）
        public required Canvas Canvas;     // 选区绘制
        public Rect Dip;                   // 该屏在虚拟屏 DIP 中的矩形
        public double Dpi;                 // 该屏 DPI
        public SD.Rectangle Phys;          // 该屏物理像素矩形（DeskBuddy 进程 PerMonitorV2 → 真物理）
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

    public SnipOverlayWindow()
    {
        // 本窗口是主逻辑宿主（不可见），真实覆盖是每屏的子窗口
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = 0; Height = 0;
        Opacity = 0;
        IsHitTestVisible = false;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object s, RoutedEventArgs e)
    {
        CaptureScreens();
        InitVisuals();
        Focusable = true; Focus();
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

    // ==================== 截屏（每屏独立，物理 1:1） ====================
    private void CaptureScreens()
    {
        _vsX = SystemParameters.VirtualScreenLeft;
        _vsY = SystemParameters.VirtualScreenTop;
        _winW = SystemParameters.VirtualScreenWidth;
        _winH = SystemParameters.VirtualScreenHeight;

        // DeskBuddy 是 PerMonitorV2 → EnumDisplayMonitors 返回真物理坐标
        var physRects = new List<SD.Rectangle>();
        var act = (Action<RECT>)(r => physRects.Add(new SD.Rectangle(r.L, r.T, r.R - r.L, r.B - r.T)));
        var gc = GCHandle.Alloc(act);
        try { EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, CollectPhys, GCHandle.ToIntPtr(gc)); }
        finally { gc.Free(); }

        // WPF 虚拟屏 DIP（SystemParameters 是物理→DIP 换算后的全局虚拟坐标）
        // 每屏 DIP 矩形：从物理矩形反推 —— 找出哪个物理矩形包含哪个 DIP 屏幕
        // 更可靠：用 WinForms.Screen（本进程 PerMonitorV2 下 Bounds 也是 DIP）
        var dipScreens = System.Windows.Forms.Screen.AllScreens;

        // 匹配：按 DIP 与物理的中心点距离（比原点匹配更稳）
        var used = new HashSet<int>();
        _mw.Clear();
        foreach (var scr in dipScreens)
        {
            var dip = new Rect(scr.Bounds.X, scr.Bounds.Y, scr.Bounds.Width, scr.Bounds.Height);
            var c = new Point(dip.X + dip.Width / 2, dip.Y + dip.Height / 2);
            int bestI = -1; double bestD = double.MaxValue;
            for (int i = 0; i < physRects.Count; i++)
            {
                if (used.Contains(i)) continue;
                var r = physRects[i];
                var pc = new Point(r.X + r.Width / 2.0, r.Y + r.Height / 2.0);
                // 物理中心换算成 DIP 需要 DPI，先粗匹配：比较宽高比 + 相对原点方向
                var d = Math.Abs((double)r.Width / dip.Width - (double)r.Height / dip.Height) * 100
                      + Math.Abs(Math.Sign(r.X) - Math.Sign(dip.X)) * 50
                      + Math.Abs(Math.Sign(r.Y) - Math.Sign(dip.Y)) * 50;
                if (d < bestD) { bestD = d; bestI = i; }
            }
            if (bestI < 0) continue;
            used.Add(bestI);
            var phys = physRects[bestI];
            double dpi = dip.Width > 0 ? phys.Width / dip.Width : 1.0;
            if (dpi <= 0.01) dpi = 1.0;

            // 该屏物理截图
            var bmp = new SD.Bitmap(phys.Width, phys.Height);
            using (var g = SD.Graphics.FromImage(bmp))
                g.CopyFromScreen(phys.X, phys.Y, 0, 0, new SD.Size(phys.Width, phys.Height));

            // 建独立覆盖窗口（该屏 DIP 矩形，WPF 自动按该屏 DPI 渲染）
            var win = new Window
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                Cursor = Cursors.Cross,
                Background = Brushes.Black,
                Left = dip.X, Top = dip.Y, Width = dip.Width, Height = dip.Height,
            };
            var img = new Image { Source = ToSource(bmp), Stretch = Stretch.Uniform };
            // 物理像素 1:1（该屏 DIP 尺寸 = 物理/DPI，Uniform 不缩放）
            var canvas = new Canvas();
            var grid = new Grid();
            grid.Children.Add(img); grid.Children.Add(canvas);
            win.Content = grid;
            win.PreviewMouseLeftButtonDown += OnMouseLeftDown;
            win.PreviewMouseMove += OnMouseMove;
            win.PreviewMouseLeftButtonUp += OnMouseLeftUp;
            win.PreviewKeyDown += OnWindowKeyDown;
            win.Show();
            _mw.Add(new MonWindow { Win = win, Img = img, Canvas = canvas, Dip = dip, Dpi = dpi, Phys = phys });
        }

        // 全屏 DIP 网格位图（供取色/输出用）：把每屏物理图重采样到 DIP 网格
        _bw = Math.Max(1, (int)Math.Round(_winW));
        _bh = Math.Max(1, (int)Math.Round(_winH));
        _bmp = new SD.Bitmap(_bw, _bh);
        using (var bg = SD.Graphics.FromImage(_bmp))
        {
            foreach (var m in _mw)
            {
                // 重新截一次物理（上面那个已被窗口占用显示）
                using var mb = new SD.Bitmap(m.Phys.Width, m.Phys.Height);
                using (var mg = SD.Graphics.FromImage(mb))
                    mg.CopyFromScreen(m.Phys.X, m.Phys.Y, 0, 0, new SD.Size(m.Phys.Width, m.Phys.Height));
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
    /// <summary>屏幕窗口内坐标 → 虚拟屏 DIP。</summary>
    private Point ToVirtual(MonWindow m, Point local) => new Point(local.X + m.Dip.X, local.Y + m.Dip.Y);

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

    private void OnMouseLeftDown(object s, MouseButtonEventArgs e)
    {
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
            var p = e.GetPosition(host.Win);
            var dx = (p.X - _dragStartPt.X); var dy = (p.Y - _dragStartPt.Y);
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
    private void RebuildUi(MonWindow host)
    {
        var c = host.Canvas;
        c.Children.Clear();
        // 虚拟 → 本屏局部
        Func<Rect, Rect> L = r => new Rect(r.X - host.Dip.X, r.Y - host.Dip.Y, r.Width, r.Height);
        var sel = _hasSel ? _sel : Rect.Empty;
        if (_hasSel)
        {
            var l = L(sel);
            // 遮罩 4 块
            AddRect(c, 0, 0, host.Dip.Width, Math.Max(0, l.Y), maskBrush: true);
            AddRect(c, 0, l.Bottom, host.Dip.Width, Math.Max(0, host.Dip.Height - l.Bottom), maskBrush: true);
            AddRect(c, 0, l.Y, Math.Max(0, l.X), l.Height, maskBrush: true);
            AddRect(c, l.Right, l.Y, Math.Max(0, host.Dip.Width - l.Right), l.Height, maskBrush: true);
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
        _loupePanel!.Measure(new Size(host.Dip.Width, host.Dip.Height));
        double lw = Math.Max(120, _loupePanel.DesiredSize.Width), lh = Math.Max(40, _loupePanel.DesiredSize.Height);
        var local = toLocal(new Rect(vp.X, vp.Y, 0, 0));
        double lx = Math.Round(local.X + 24), ly = Math.Round(local.Y + 24);
        double panelSize = LoupeN * CellPx;
        if (lx + lw > host.Dip.Width - 8) lx = Math.Round(local.X - lw - 24);
        if (ly + lh + panelSize + 20 > host.Dip.Height - 8) ly = Math.Round(local.Y - lh - panelSize - 32);
        if (lx < 8) lx = 8; if (ly < 8) ly = 8;
        Canvas.SetLeft(_loupe, lx + 8); Canvas.SetTop(_loupe, ly + 8);
        Canvas.SetLeft(_loupePanel, lx); Canvas.SetTop(_loupePanel, ly + panelSize + 12);
        if (_loupe.Parent is Panel p) p.Children.Remove(_loupe);
        if (_loupePanel.Parent is Panel p2) p2.Children.Remove(_loupePanel);
        c.Children.Add(_loupe); c.Children.Add(_loupePanel);
    }

    // ==================== 操作条 ====================
    private void ShowToolbar(MonWindow host)
    {
        if (_toolbar == null)
        {
            _toolbar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1E)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(6, 4, 6, 4)
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(MkBtn("复制", CopySelection));
            sp.Children.Add(MkBtn("保存", SaveSelection));
            sp.Children.Add(MkBtn("贴图", PinSelection));
            _toolbar.Child = sp;
        }
        if (_toolbar.Parent is Panel p) p.Children.Remove(_toolbar);
        host.Canvas.Children.Add(_toolbar);
        _toolbar.Measure(new Size(host.Dip.Width, host.Dip.Height));
        double tw = _toolbar.DesiredSize.Width, th = _toolbar.DesiredSize.Height;
        double x = _sel.Right - host.Dip.X - tw, y = _sel.Bottom - host.Dip.Y + 8;
        if (y + th > host.Dip.Height - 8) y = _sel.Bottom - host.Dip.Y - th - 8;
        if (x < 8) x = 8;
        Canvas.SetLeft(_toolbar, x); Canvas.SetTop(_toolbar, y);
    }

    private Button MkBtn(string text, Action act)
    {
        var b = new Button { Content = text, Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)), Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 12, Padding = new Thickness(10, 5, 10, 5), Cursor = Cursors.Hand };
        b.Click += (s, e) => { act(); e.Handled = true; };
        return b;
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
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图片 (*.png)|*.png", Title = "保存截图", FileName = "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") };
        if (dlg.ShowDialog(this) != true) return;
        try { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(src)); using var fs = File.Create(dlg.FileName); enc.Save(fs); CloseAll(); }
        catch (Exception ex) { MessageBox.Show(this, "保存失败：" + ex.Message, "错误"); }
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

    private void CloseAll()
    {
        foreach (var m in _mw) { try { m.Win.Close(); } catch { } }
        _bmp?.Dispose(); _bmp = null; _px = null;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var m in _mw) { try { if (m.Win.IsLoaded) m.Win.Close(); } catch { } }
        _bmp?.Dispose(); _bmp = null; _px = null;
        base.OnClosed(e);
    }
}