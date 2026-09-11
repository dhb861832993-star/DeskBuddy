using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Interop;
using SD = System.Drawing;

namespace DeskBuddy.Tools;

/// <summary>Snipaste 式截图覆盖层：冻结全屏 → 拖选 + 控制点调整 + 放大镜取色 → 复制/保存/贴图。</summary>
public partial class SnipOverlayWindow : Window
{
    // ===== 冻结层 =====
    private SD.Bitmap? _bmp;              // 全屏截图（像素坐标）
    private byte[]? _px;                  // 像素缓存（BGRA）
    private int _bw, _bh, _stride;
    private double _dpi = 1.0;            // DIP → 像素比例
    private double _winW, _winH;

    // ===== 选区（DIP，窗口坐标） =====
    private Rect _sel = Rect.Empty;
    private bool _hasSel;

    // ===== 交互状态 =====
    private bool _drawing;                // 正在拖新选区
    private Point _drawStart;
    private int _dragKind;                // 0=无 1=移动 2..9=8个控制点
    private Point _dragStartPt; private Rect _dragStartSel;

    // ===== 可视元素 =====
    private readonly Rectangle[] _masks = new Rectangle[4];   // 遮罩（上下左右）
    private readonly Rectangle _border = new();
    private readonly Rectangle[] _handles = new Rectangle[8];  // 8 控制点
    private readonly TextBlock _sizeLabel = new();
    private Border? _toolbar;                                     // 操作条
    // 放大镜
    private readonly Canvas _loupe = new();
    private readonly Rectangle[] _loupeCells = new Rectangle[LoupeN * LoupeN];
    private readonly Rectangle _loupeCenter = new();
    private readonly TextBlock _loupeText = new();
    private const int LoupeN = 9;          // 9x9 网格
    private const int CellPx = 12;         // 每格像素

    public byte[]? PixelCache => _px;
    public int BmpWidth => _bw; public int BmpHeight => _bh;
    public double Dpi => _dpi;

    public SnipOverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object s, RoutedEventArgs e)
    {
        CaptureScreen();
        InitVisuals();
        MouseLeftButtonDown += OnMouseLeftDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftUp;
        Focus();
    }

    // ==================== 截屏（冻结） ====================
    private void CaptureScreen()
    {
        var vsX = SystemParameters.VirtualScreenLeft;
        var vsY = SystemParameters.VirtualScreenTop;
        var vsW = SystemParameters.VirtualScreenWidth;
        var vsH = SystemParameters.VirtualScreenHeight;
        Left = vsX; Top = vsY;
        _winW = vsW; _winH = vsH;
        Width = vsW; Height = vsH;

        using var g = SD.Graphics.FromHwnd(IntPtr.Zero);
        _dpi = g.DpiX / 96.0;
        if (_dpi <= 0) _dpi = 1.0;

        _bw = Math.Max(1, (int)Math.Round(vsW * _dpi));
        _bh = Math.Max(1, (int)Math.Round(vsH * _dpi));

        _bmp = new SD.Bitmap(_bw, _bh);
        using (var bg = SD.Graphics.FromImage(_bmp))
        {
            bg.CopyFromScreen((int)Math.Round(vsX * _dpi), (int)Math.Round(vsY * _dpi), 0, 0, new SD.Size(_bw, _bh));
        }

        // 像素缓存（放大镜取色）
        var data = _bmp.LockBits(new SD.Rectangle(0, 0, _bw, _bh), SD.Imaging.ImageLockMode.ReadOnly, SD.Imaging.PixelFormat.Format32bppArgb);
        _stride = data.Stride;
        _px = new byte[Math.Abs(data.Stride) * _bh];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, _px, 0, _px.Length);
        _bmp.UnlockBits(data);

        // 显示冻结层
        IntPtr hBmp = _bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            FrozenImage.Source = src;
            FrozenImage.Width = _bw / _dpi;   // 像素 → DIP 显示
            FrozenImage.Height = _bh / _dpi;
        }
        finally { DeleteObject(hBmp); }
    }

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    private Color GetPixelAt(double dipX, double dipY)
    {
        if (_px == null) return Colors.Transparent;
        int x = (int)Math.Round(dipX * _dpi), y = (int)Math.Round(dipY * _dpi);
        x = Math.Clamp(x, 0, _bw - 1); y = Math.Clamp(y, 0, _bh - 1);
        int i = y * _stride + x * 4;
        return Color.FromArgb(_px[i + 3], _px[i + 2], _px[i + 1], _px[i]);
    }

    // ==================== 初始化可视元素 ====================
    private void InitVisuals()
    {
        // 遮罩
        var maskBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
        for (int i = 0; i < 4; i++)
        {
            _masks[i] = new Rectangle { Fill = maskBrush, IsHitTestVisible = false };
            OverlayCanvas.Children.Add(_masks[i]);
        }
        // 边框
        _border.Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xFF)); _border.StrokeThickness = 1.4; _border.IsHitTestVisible = false;
        OverlayCanvas.Children.Add(_border);
        // 尺寸标签
        _sizeLabel.Foreground = Brushes.White; _sizeLabel.FontSize = 11.5; _sizeLabel.IsHitTestVisible = false;
        var sizeBg = new Border { Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1E)), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Child = _sizeLabel };
        OverlayCanvas.Children.Add(sizeBg);
        _sizeLabel.Tag = sizeBg;
        for (int i = 0; i < 8; i++)
        {
            _handles[i] = new Rectangle { Width = 9, Height = 9, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), StrokeThickness = 1.2, Cursor = Cursors.SizeAll };
            int idx = i;
            _handles[i].MouseLeftButtonDown += (s, e) => { StartHandleDrag(idx + 2, e); };
            OverlayCanvas.Children.Add(_handles[i]);
        }
        // 放大镜
        _loupe.IsHitTestVisible = false;
        for (int i = 0; i < LoupeN * LoupeN; i++)
        {
            var cell = new Rectangle { Width = CellPx, Height = CellPx, Stroke = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00)), StrokeThickness = 0.5 };
            _loupeCells[i] = cell; _loupe.Children.Add(cell);
        }
        _loupeCenter.Width = CellPx; _loupeCenter.Height = CellPx; _loupeCenter.Stroke = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)); _loupeCenter.StrokeThickness = 1.6; _loupeCenter.Fill = Brushes.Transparent;
        _loupe.Children.Add(_loupeCenter);
        _loupeText.Foreground = Brushes.White; _loupeText.FontSize = 11;
        _loupeText.TextAlignment = TextAlignment.Center;
        var loupeBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = _loupeText
        };
        OverlayCanvas.Children.Add(_loupe);
        OverlayCanvas.Children.Add(loupeBorder);
        _loupe.Tag = loupeBorder;
        UpdateVisuals(mouse: null);
    }

    // ==================== 鼠标交互 ====================
    private void OnMouseLeftDown(object s, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_hasSel && _sel.Contains(p))
        {
            StartHandleDrag(1, e);   // 移动选区
            return;
        }
        // 新选区
        _drawing = true; _drawStart = p;
        _sel = new Rect(p, p); _hasSel = true;
        if (_toolbar != null) _toolbar.Visibility = Visibility.Collapsed;
        Mouse.Capture(this);
        e.Handled = true;
    }

    private void StartHandleDrag(int kind, MouseButtonEventArgs e)
    {
        _dragKind = kind;
        _dragStartPt = e.GetPosition(this);
        _dragStartSel = _sel;
        Mouse.Capture(this);
        e.Handled = true;
    }

    private void OnMouseMove(object s, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drawing)
        {
            _sel = new Rect(_drawStart, p);
            UpdateVisuals(p);
        }
        else if (_dragKind > 0)
        {
            var dx = p.X - _dragStartPt.X; var dy = p.Y - _dragStartPt.Y;
            if (_dragKind == 1) _sel = new Rect(_dragStartSel.X + dx, _dragStartSel.Y + dy, _dragStartSel.Width, _dragStartSel.Height);
            else ApplyHandleDrag(dx, dy);
            _sel = ClampSel(_sel);
            UpdateVisuals(p);
        }
        else
        {
            UpdateVisuals(p);
        }
        e.Handled = true;
    }

    private void ApplyHandleDrag(double dx, double dy)
    {
        // 控制点: 2=左上 3=上中 4=右上 5=右中 6=右下 7=下中 8=左下 9=左中
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
        _sel = Rect.Intersect(new Rect(Math.Min(l, r), Math.Min(t, b), Math.Abs(r - l), Math.Abs(b - t)), new Rect(0, 0, _winW, _winH));
    }

    private Rect ClampSel(Rect r)
    {
        double x = Math.Clamp(r.X, 0, _winW), y = Math.Clamp(r.Y, 0, _winH);
        double w = Math.Min(r.Width, _winW - x), h = Math.Min(r.Height, _winH - y);
        return new Rect(x, y, Math.Max(0, w), Math.Max(0, h));
    }

    private void OnMouseLeftUp(object s, MouseButtonEventArgs e)
    {
        var wasDrawing = _drawing || _dragKind > 0;
        _drawing = false; _dragKind = 0;
        Mouse.Capture(null);
        if (wasDrawing && _sel.Width > 6 && _sel.Height > 6)
        {
            ShowToolbar();
            UpdateVisuals(e.GetPosition(this));
        }
        else if (_sel.Width <= 6 || _sel.Height <= 6)
        {
            _hasSel = false;
            UpdateVisuals(e.GetPosition(this));
        }
        e.Handled = true;
    }

    private void OnWindowKeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (e.Key == Key.Enter) { if (_hasSel && _sel.Width > 4) CopySelection(); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.S) { if (_hasSel && _sel.Width > 4) SaveSelection(); e.Handled = true; return; }
        // 方向键微调选区
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
            UpdateVisuals(Mouse.GetPosition(this));
            e.Handled = true;
        }
    }

    // ==================== 可视更新 ====================
    private void UpdateVisuals(Point? mouse)
    {
        var sel = _hasSel ? _sel : Rect.Empty;
        // 遮罩（上下左右）
        if (_hasSel)
        {
            SetRect(_masks[0], 0, 0, _winW, Math.Max(0, sel.Y));
            SetRect(_masks[1], 0, sel.Bottom, _winW, Math.Max(0, _winH - sel.Bottom));
            SetRect(_masks[2], 0, sel.Y, Math.Max(0, sel.X), sel.Height);
            SetRect(_masks[3], sel.Right, sel.Y, Math.Max(0, _winW - sel.Right), sel.Height);
            foreach (var m in _masks) m.Visibility = Visibility.Visible;
        }
        else foreach (var m in _masks) m.Visibility = Visibility.Collapsed;

        // 边框 + 控制点 + 尺寸
        bool show = _hasSel && sel.Width > 2;
        _border.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _sizeLabel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_sizeLabel.Tag is Border bg) bg.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        foreach (var h in _handles) h.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            SetRect(_border, sel.X, sel.Y, sel.Width, sel.Height);
            // 控制点位置
            var hx = new[] { sel.X, sel.X + sel.Width / 2, sel.Right, sel.Right, sel.Right, sel.X + sel.Width / 2, sel.X, sel.X };
            var hy = new[] { sel.Y, sel.Y, sel.Y, sel.Y + sel.Height / 2, sel.Bottom, sel.Bottom, sel.Bottom, sel.Y + sel.Height / 2 };
            for (int i = 0; i < 8; i++) { Canvas.SetLeft(_handles[i], hx[i] - 4.5); Canvas.SetTop(_handles[i], hy[i] - 4.5); }
            _sizeLabel.Text = $"{(int)Math.Round(sel.Width * _dpi)} × {(int)Math.Round(sel.Height * _dpi)}";
            if (_sizeLabel.Tag is Border sbg)
            {
                double lx = sel.X; double ly = sel.Y - 30;
                if (ly < 4) ly = sel.Bottom + 6;
                Canvas.SetLeft(sbg, lx); Canvas.SetTop(sbg, ly);
            }
        }

        // 放大镜
        if (mouse is { } mp)
        {
            UpdateLoupe(mp);
        }
    }

    private static void SetRect(Rectangle r, double x, double y, double w, double h)
    {
        Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
        r.Width = Math.Max(0, w); r.Height = Math.Max(0, h);
    }

    private void UpdateLoupe(Point mp)
    {
        var loupeBorder = (Border)_loupe.Tag!;
        int half = LoupeN / 2;
        double px = mp.X, py = mp.Y;
        // 中心颜色
        var c = GetPixelAt(px, py);
        // 网格填色
        for (int gy = 0; gy < LoupeN; gy++)
        for (int gx = 0; gx < LoupeN; gx++)
        {
            int i = gy * LoupeN + gx;
            var cellColor = GetPixelAt(px + (gx - half) / _dpi, py + (gy - half) / _dpi);
            _loupeCells[i].Fill = new SolidColorBrush(cellColor);
        }
        _loupeCenter.Width = CellPx; _loupeCenter.Height = CellPx;
        Canvas.SetLeft(_loupeCenter, half * CellPx); Canvas.SetTop(_loupeCenter, half * CellPx);
        var panelSize = LoupeN * CellPx;
        _loupe.Width = panelSize; _loupe.Height = panelSize;
        // 文本：HEX + 坐标 + RGB
        _loupeText.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}  RGB({c.R},{c.G},{c.B})\n({(int)Math.Round(px * _dpi)}, {(int)Math.Round(py * _dpi)})";
        // 定位：鼠标右下方，越界翻转
        loupeBorder.Measure(new Size(_winW, _winH));
        double lw = loupeBorder.DesiredSize.Width, lh = loupeBorder.DesiredSize.Height;
        double lx = mp.X + 24, ly = mp.Y + 24;
        if (lx + lw > _winW - 8) lx = mp.X - lw - 24;
        if (ly + lh > _winH - 8) ly = mp.Y - lh - 24;
        if (lx < 8) lx = 8; if (ly < 8) ly = 8;
        // 放大镜网格在信息面板内
        Canvas.SetLeft(_loupe, lx + 8); Canvas.SetTop(_loupe, ly + 8);
        Canvas.SetLeft(loupeBorder, lx); Canvas.SetTop(loupeBorder, ly + panelSize + 12);
        loupeBorder.Visibility = Visibility.Visible;
    }

    // ==================== 操作条 ====================
    private void ShowToolbar()
    {
        if (_toolbar == null)
        {
            _toolbar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1E)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(6, 4, 6, 4)
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(MakeToolBtn("复制", () => CopySelection()));
            sp.Children.Add(MakeToolBtn("保存", () => SaveSelection()));
            sp.Children.Add(MakeToolBtn("贴图", () => PinSelection()));
            _toolbar.Child = sp;
            OverlayCanvas.Children.Add(_toolbar);
        }
        _toolbar.Visibility = Visibility.Visible;
        _toolbar.Measure(new Size(_winW, _winH));
        double tw = _toolbar.DesiredSize.Width, th = _toolbar.DesiredSize.Height;
        double x = _sel.Right - tw, y = _sel.Bottom + 8;
        if (y + th > _winH - 8) y = _sel.Bottom - th - 8;      // 下方放不下则放选区内
        if (x < 8) x = 8;
        Canvas.SetLeft(_toolbar, x); Canvas.SetTop(_toolbar, y);
    }

    private Button MakeToolBtn(string text, Action act)
    {
        var b = new Button
        {
            Content = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 12,
            Padding = new Thickness(10, 5, 10, 5),
            Cursor = Cursors.Hand
        };
        b.Click += (s, e) => { act(); e.Handled = true; };
        return b;
    }

    // ==================== 输出 ====================
    /// <summary>从全屏截图中截取选区（像素精确）。</summary>
    private BitmapSource? RenderSelection()
    {
        if (_bmp == null || !_hasSel) return null;
        int x = (int)Math.Round(_sel.X * _dpi), y = (int)Math.Round(_sel.Y * _dpi);
        int w = (int)Math.Round(_sel.Width * _dpi), h = (int)Math.Round(_sel.Height * _dpi);
        x = Math.Clamp(x, 0, _bw - 1); y = Math.Clamp(y, 0, _bh - 1);
        w = Math.Clamp(w, 1, _bw - x); h = Math.Clamp(h, 1, _bh - y);
        using var part = _bmp.Clone(new SD.Rectangle(x, y, w, h), _bmp.PixelFormat);
        IntPtr hBmp = part.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DeleteObject(hBmp); }
    }

    private void CopySelection()
    {
        var src = RenderSelection();
        if (src == null) return;
        try
        {
            Clipboard.SetImage(src);
            Close();
        }
        catch { }
    }

    private void SaveSelection()
    {
        var src = RenderSelection();
        if (src == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图片 (*.png)|*.png", Title = "保存截图", FileName = "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(src));
            using var fs = File.Create(dlg.FileName); enc.Save(fs);
            Close();
        }
        catch (Exception ex) { MessageBox.Show(this, "保存失败：" + ex.Message, "错误"); }
    }

    private void PinSelection()
    {
        var src = RenderSelection();
        if (src == null) return;
        double dipX = _sel.X + Left, dipY = _sel.Y + Top;
        Close();
        var pin = new PinWindow(src, dipX, dipY, _sel.Width, _sel.Height);
        pin.Show();
    }

    protected override void OnClosed(EventArgs e)
    {
        _bmp?.Dispose(); _bmp = null; _px = null;
        base.OnClosed(e);
    }
}