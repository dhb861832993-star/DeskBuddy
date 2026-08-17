using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DeskBuddy.Services;

namespace DeskBuddy;

/// <summary>
/// 右下角索引进度条：大目录加入 Windows Search 索引时显示实时进度。
/// 通过轮询 SystemIndex 计数（OleDb）与目录总量估算进度；
/// 全部索引完成或长时间无进展后自动关闭，点击可手动关闭。
/// </summary>
public partial class IndexProgressWindow : Window
{
    private static IndexProgressWindow? _active;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string[] _roots = Array.Empty<string>();
    private long _lastIndexed = -1;
    private int _noProgressTicks;
    private DateTime _startedAt;
    private long _total = -1;

    public IndexProgressWindow()
    {
        InitializeComponent();
        Left = SystemParameters.WorkArea.Right - Width - 16;
        Top = SystemParameters.WorkArea.Bottom - Height - 16;
    }

    /// <summary>若当前没有进度条在显示，则弹出（仅当索引尚未完成时；异步判断，不阻塞 UI）。</summary>
    public static async void ShowIfLarge(string[] roots)
    {
        if (_active != null) return;
        bool large;
        try
        {
            large = await Task.Run(() => IndexScope.IsLargeScope(roots));
        }
        catch { return; }
        if (!large) return;

        var w = new IndexProgressWindow();
        _active = w;
        w._roots = roots;
        w._startedAt = DateTime.UtcNow;
        w.Show();
    }

    /// <summary>搜索范围内存在正在爬取的大目录（如用户手动加入、系统正在索引）时也显示进度条。</summary>
    public static async void ShowIfCrawling(string[] roots)
    {
        if (_active != null) return;
        bool crawling;
        try
        {
            crawling = await Task.Run(() => IndexScope.IsCrawling(roots));
        }
        catch { return; }
        if (!crawling) return;

        var w = new IndexProgressWindow();
        _active = w;
        w._roots = roots;
        w._startedAt = DateTime.UtcNow;
        w.Show();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _timer.Tick += OnTick;
        _timer.Start();
        OnTick(this, EventArgs.Empty);
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        try
        {
            if (_total < 0)
            {
                // 首次：后台统计总量（大目录可能耗时，先显示已索引数）
                var roots = _roots;
                _ = Task.Run(() => IndexScope.CountFiles(roots)).ContinueWith(t =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        _total = t.Result;
                        UpdateUi();
                    });
                });
            }

            long indexed = await Task.Run(() => IndexScope.CountIndexed(_roots));
            UpdateUi(indexed);
        }
        catch { }
    }

    private void UpdateUi(long? indexed = null)
    {
        var shown = indexed ?? -1;
        if (shown >= 0)
        {
            if (_lastIndexed >= 0 && shown == _lastIndexed)
            {
                _noProgressTicks++;
                if (_noProgressTicks > 30) { Close(); return; } // 60 秒无进展 → 关闭（容忍爬取批次间隙）
            }
            else if (shown > _lastIndexed)
            {
                _noProgressTicks = 0;
            }
            _lastIndexed = shown;

            var totalText = _total > 0 ? FormatCount(_total) : "统计中…";
            DetailText.Text = $"{_roots[0]} — 已索引 {FormatCount(shown)} / {totalText}";

            if (_total > 0)
            {
                var pct = Math.Min(100.0, shown * 100.0 / _total);
                Progress.Value = pct;
                PercentText.Text = $"{(int)pct}%";
                if (shown >= _total)
                {
                    PercentText.Text = "100% — 索引完成";
                    _ = Task.Delay(2500).ContinueWith(_ => Dispatcher.BeginInvoke(Close));
                    _timer.Stop();
                }
            }
            else
            {
                PercentText.Text = $"已索引 {FormatCount(shown)} 个文件";
            }
        }

        // 最长显示 30 分钟，防止异常情况常驻
        if ((DateTime.UtcNow - _startedAt).TotalMinutes > 30)
        {
            _timer.Stop();
            Close();
        }
    }

    private static string FormatCount(long n)
    {
        if (n >= 100_000_000) return $"{(n / 100_000_000.0):F1} 亿";
        if (n >= 10_000) return $"{(n / 10000.0):F1} 万";
        return n.ToString("N0");
    }

    private void OnDismiss(object sender, MouseButtonEventArgs e) => Close();

    /// <summary>关闭当前显示的进度条（主菜单按 ESC 时联动关闭）。</summary>
    public static void CloseIfActive()
    {
        var w = _active;
        if (w != null)
        {
            try { w.Dispatcher.BeginInvoke(w.Close); } catch { }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        if (ReferenceEquals(_active, this)) _active = null;
        base.OnClosed(e);
    }
}
