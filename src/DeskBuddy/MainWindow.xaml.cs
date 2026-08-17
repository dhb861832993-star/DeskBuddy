using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeskBuddy.Models;
using DeskBuddy.Services;

namespace DeskBuddy;

/// <summary>菜单中一个条目的视图模型。</summary>
public sealed class ItemVm : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>对应配置条目（文件搜索结果为 null）。</summary>
    public DeskBuddyItem? Source { get; init; }
    public required string Name { get; init; }
    public required string Subtitle { get; init; }
    private ImageSource? _icon;
    /// <summary>图标（文件结果先为 null 秒出，后台异步补充真实图标）。</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon)));
        }
    }
    public string? Glyph { get; init; }
    public required Brush IconBg { get; init; }
    public required string SearchText { get; init; }
    /// <summary>item=菜单条目；file=文件搜索结果。</summary>
    public string Kind { get; init; } = "item";
    /// <summary>文件搜索结果的完整路径。</summary>
    public string LaunchPath { get; init; } = "";
    /// <summary>文件搜索结果的修改时间（格式化显示，如 2026-08-17 18:30）。</summary>
    public string TimeText { get; init; } = "";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private AppConfig _config = new();
    private Theme _theme = Theme.Dark;
    private List<ItemVm> _all = new();
    private List<ItemVm> _filtered = new();
    private List<ItemVm> _fileResults = new();
    private CancellationTokenSource? _fileSearchCts;
    private bool _justOpened;
    private bool _suppressHide;
    private DateTime _lastLaunchTime = DateTime.MinValue;
    private DispatcherTimer? _hideTimer;

    /// <summary>配置被重新加载（App 据此更新热键检测器）。</summary>
    public event Action<AppConfig>? ConfigChanged;

    public MainWindow()
    {
        InitializeComponent();
        RoundedWindow.Apply(this, RootCard.CornerRadius.TopLeft);
    }

    // ==================== 显示 / 隐藏 ====================

    public void ShowMenu()
    {
        // 刚打开的短暂窗口内忽略 Deactivated，避免托盘点击引发的闪烁
        StopHideTimer(); // 取消未完成的隐藏，防止 呼出→隐藏 竞态
        _justOpened = true;
        _suppressHide = true;
        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        guard.Tick += (_, _) => { guard.Stop(); _justOpened = false; };
        guard.Start();

        ReloadConfig();
        ApplyTheme();

        RefreshItems();
        ApplyLayoutMode();
        CancelFileSearch();
        _fileResults = new List<ItemVm>();
        // 提前预热文件索引（后台），让首次搜索就快
        if (_config.EnableFileSearch && _config.SearchRoots.Count > 0)
        {
            var roots = _config.SearchRoots.Where(r => !string.IsNullOrWhiteSpace(r)).ToArray();
            FileIndex.EnsureBuilding(roots);
            // 自有 USN 引擎（Listary 同技术路线，不依赖 Windows Search）
            if (_config.FileSearchBackend != "builtin")
            {
                UsnIndex.EnsureBuilding(roots);
            }
            // 自动把搜索根目录加入 Windows Search 索引范围；仅「Windows Search 后端」才显示爬取进度条
            // （USN 引擎/auto 下搜索走自有索引，系统爬取进度不再打扰用户）
            if (_config.FileSearchBackend == "wsearch" && WindowsSearch.IsAvailable())
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        var added = IndexScope.EnsureScopes(roots);
                        if (added.Count > 0)
                        {
                            Dispatcher.BeginInvoke(() => IndexProgressWindow.ShowIfLarge(added.ToArray()));
                        }
                        else
                        {
                            // 范围已配置但仍在爬取的大目录 → 显示进度条
                            Dispatcher.BeginInvoke(() => IndexProgressWindow.ShowIfCrawling(roots));
                        }
                    }
                    catch { }
                });
            }
        }
        SearchBox.Text = "";
        UpdateFilter();
        PositionWindow();

        if (!IsVisible)
        {
            // 瞬时显示（不做透明度动画）：分层窗口的淡入动画会引发
            // DWM 合成时机的“闪一帧”问题，瞬时显示最干净
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            Show();
        }

        // 延迟到窗口真正显示后再抢焦点（绕过 Windows 前台锁）
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsVisible) return; // 窗口已隐藏则不再聚焦，避免 TSF 竞态
            ForceActivate();
            SearchBox.Focus();
            SearchBox.SelectAll();
        }));
        _suppressHide = false;
        _shownTime = DateTime.UtcNow;
        StartHideTimer(); // 显示期间持续监视前台/鼠标状态，点击别处自动隐藏
    }

    /// <summary>通过 Win32 强行把窗口带到前台并激活。</summary>
    private void ForceActivate()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var fg = GetForegroundWindow();
        if (fg == hwnd) return;

        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = false;
        if (fgThread != 0 && fgThread != thisThread)
        {
            attached = AttachThreadInput(thisThread, fgThread, true);
        }
        try
        {
            SetForegroundWindow(hwnd);
            Activate();
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, fgThread, false);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public void HideMenu()
    {
        DebugLog.Write("HideMenu called");
        StopHideTimer();
        if (!IsVisible) return;
        ItemMenuPopup.IsOpen = false;
        ResetDragState();
        // 先释放文本框焦点再隐藏，避免 TSF（输入法）在窗口隐藏时崩溃
        Keyboard.ClearFocus();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _hideTimer.Tick += (_, _) =>
        {
            StopHideTimer();
            if (IsVisible) PerformHide();
        };
        _hideTimer.Start();
    }

    /// <summary>真正隐藏窗口：先复位透明度（清除动画、基值恢复 1），
    /// 避免下次显示时第一帧带着上次动画的残留值“闪”一下。</summary>
    private void PerformHide()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        Hide();
    }

    // ==================== 配置 / 主题 ====================

    private void ReloadConfig()
    {
        _config = ConfigManager.Load();
        _theme = Theme.From(_config.Theme);
        ConfigChanged?.Invoke(_config);
    }

    private void ApplyTheme()
    {
        Resources["TextPrimary"] = Frozen(_theme.TextPrimary);
        Resources["TextSecondary"] = Frozen(_theme.TextSecondary);
        Resources["HoverBg"] = Frozen(_theme.HoverBg);
        Resources["SelectedBg"] = Frozen(_theme.SelectedBg);
        Resources["CardBorder"] = Frozen(_theme.BorderColor);
        Resources["SeparatorBrush"] = Frozen(_theme.Separator);
        Resources["SearchGlyphBrush"] = Frozen(_theme.TextSecondary);
        Resources["CardBg"] = new SolidColorBrush(_theme.CardTint) { Opacity = _theme.CardAlpha };
        RootCard.Background = Resources["CardBg"] as Brush;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ==================== 条目 ====================

    private void RefreshItems()
    {
        _all = new List<ItemVm>();
        foreach (var item in _config.Items)
        {
            if (item.Hidden) continue;
            var vm = BuildVm(item);
            if (vm != null) _all.Add(vm);
        }
    }

    private ItemVm? BuildVm(DeskBuddyItem item)
    {
        string subtitle;
        ImageSource? icon = null;
        string? glyph;
        Color iconBg;

        switch (item.Type)
        {
            case "url":
                subtitle = item.Path;
                break;

            case "folder":
                subtitle = Environment.ExpandEnvironmentVariables(item.Path);
                break;

            case "file":
                subtitle = Environment.ExpandEnvironmentVariables(item.Path);
                break;

            case "command":
                subtitle = string.IsNullOrWhiteSpace(item.Args)
                    ? item.Path
                    : $"{item.Path} {item.Args}";
                break;

            default: // app
                var exe = Launcher.Resolve(item.Path);
                subtitle = exe ?? $"未找到：{item.Path}";
                icon = exe != null ? IconProvider.GetFileIcon(exe, item.Icon) : null;
                break;
        }
        glyph = ItemVisuals.GlyphFor(item.Type);
        iconBg = ItemVisuals.ColorFor(item.Type);

        var bg = new SolidColorBrush(iconBg);
        bg.Freeze();

        var search = $"{item.Name} {item.Keywords} {subtitle}".ToLowerInvariant();

        return new ItemVm
        {
            Source = item,
            Name = string.IsNullOrWhiteSpace(item.Name) ? item.Path : item.Name,
            Subtitle = subtitle,
            Icon = icon,
            Glyph = glyph,
            IconBg = bg,
            SearchText = search
        };
    }

    private void UpdateFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 菜单条目匹配（永远排在前面，不与文件结果混在一起）
        _filtered = tokens.Length == 0
            ? _all
            : _all.Where(v => tokens.All(t =>
                  v.SearchText.Contains(t, StringComparison.CurrentCultureIgnoreCase))).ToList();

        // 文件搜索：启用且有关键字时异步扫描指定范围，结果追加在下方
        CancelFileSearch();
        if (tokens.Length > 0 && _config.EnableFileSearch && _config.SearchRoots.Count > 0)
        {
            _fileResults = new List<ItemVm>();
            StartFileSearch(query);
        }
        else
        {
            _fileResults = new List<ItemVm>();
        }

        RebuildDisplay(query, tokens.Length > 0);
    }

    /// <summary>刷新菜单条目列表与文件结果列表。</summary>
    private void RebuildDisplay(string query, bool searching)
    {
        // 菜单条目（文件结果单独在 FileList 显示，不混入宫格）；内容未变时跳过重建
        if (!ReferenceEquals(ItemList.ItemsSource, _filtered))
        {
            ItemList.ItemsSource = _filtered;
            ItemList.SelectedIndex = _filtered.Count > 0 ? 0 : -1;
        }

        // 文件结果：独立列表（名称+路径+修改时间，最新在前）
        if (_fileResults.Count > 0)
        {
            FileSectionTitle.Text = $"文件（{_fileResults.Count} 个匹配）";
            if (!ReferenceEquals(FileList.ItemsSource, _fileResults)) FileList.ItemsSource = _fileResults;
            FileSection.Visibility = Visibility.Visible;
            // 菜单无匹配时自动选中第一个文件，Enter 可直接打开
            if (_filtered.Count == 0) FileList.SelectedIndex = 0;
        }
        else
        {
            FileSection.Visibility = Visibility.Collapsed;
            if (FileList.ItemsSource != null) FileList.ItemsSource = null;
        }

        DebugLog.Write($"RebuildDisplay: menu={_filtered.Count} files={_fileResults.Count}");

        if (searching)
            CountText.Text = _fileResults.Count > 0
                ? $"{_filtered.Count} 个菜单项 · 文件 {_fileResults.Count}"
                : $"{_filtered.Count} 个菜单项";
        else
            CountText.Text = _filtered.Count == _all.Count
                ? $"{_all.Count} 个项目"
                : $"{_filtered.Count} / {_all.Count} 个项目";

        Placeholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
        if (_filtered.Count == 0 && _fileResults.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            var aiOk = ((App)Application.Current).CurrentConfig.AiEnabled;
            EmptyHintText.Text = !string.IsNullOrEmpty(query) && aiOk
                ? $"✨ 用 AI 回答：{query}"
                : "没有匹配的条目";
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
        }
        // 结果数量变化时自动调整窗口高度（文件结果多时向下扩展，减少滚动）
        PositionWindow();
        // 底部右侧不再显示常驻操作说明，仅保留瞬时的操作反馈（如拖拽添加成功）
        FooterHint.Text = "";
    }

    // ==================== 文件搜索 ====================

    private void CancelFileSearch()
    {
        _fileSearchCts?.Cancel();
        _fileSearchCts?.Dispose();
        _fileSearchCts = null;
    }

    /// <summary>文件搜索结果上限（窗口会自动扩展高度，放宽到 100 条）。</summary>
    private const int MaxFileResults = 100;

    /// <summary>启动文件搜索：USN 引擎就绪时同步毫秒级匹配、当帧显示（像 Listary）；其余走异步兜底。</summary>
    private void StartFileSearch(string query)
    {
        var roots = _config.SearchRoots.Where(r => !string.IsNullOrWhiteSpace(r)).ToArray();
        if (roots.Length == 0) return;
        FileIndex.EnsureBuilding(roots); // 后台构建/刷新索引，不阻塞

        var backend = _config.FileSearchBackend;

        // USN 引擎就绪 → 同步搜索（前缀桶毫秒级），结果与击键同一帧显示
        if (backend == "usn" || (backend == "auto" && UsnIndex.IsReady))
        {
            List<(string Name, string Path)> found;
            try
            {
                found = UsnIndex.Search(query, roots, MaxFileResults)
                    .Select(p => (System.IO.Path.GetFileName(p), p))
                    .ToList();
            }
            catch { found = new List<(string, string)>(); }

            if (found.Count == 0 && backend == "auto")
            {
                // auto：USN 无结果 → 异步回退 Windows Search / 内置索引
                var q = query; var rts = roots;
                _ = Task.Run(async () =>
                {
                    List<(string Name, string Path)> fb = new();
                    if (WindowsSearch.IsAvailable())
                        fb = await Task.Run(() => WindowsSearch.Search(q, rts, MaxFileResults));
                    if (fb.Count == 0 && FileIndex.IsReady) fb = FileIndex.Search(q, MaxFileResults);
                    if (SearchBox.Text?.Trim() != q) return;
                    Dispatcher.BeginInvoke(() => ApplyFileResults(fb, q));
                });
                ApplyFileResults(found, query);
                return;
            }
            ApplyFileResults(found, query);
            return;
        }

        // 异步路径（Windows Search / 内置索引 / 实时扫描兜底）
        var cts = new CancellationTokenSource();
        _fileSearchCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            List<(string Name, string Path)> found;
            if (backend == "wsearch" || (backend == "auto" && WindowsSearch.IsAvailable()))
            {
                // Windows Search 系统索引
                found = await Task.Run(() => WindowsSearch.Search(query, roots, MaxFileResults), token);
                if (found.Count == 0 && backend == "auto" && FileIndex.IsReady)
                {
                    // auto 模式：Windows Search 无结果 → 回退内置索引
                    found = FileIndex.Search(query, MaxFileResults);
                }
            }
            else if (FileIndex.IsReady)
            {
                found = FileIndex.Search(query, MaxFileResults);
            }
            else
            {
                try { await Task.Delay(200, token); } catch { return; }
                if (token.IsCancellationRequested) return;
                found = FileSearcher.Search(roots, query, token, maxResults: MaxFileResults);
            }
            if (token.IsCancellationRequested) return;

            // 在后台线程完成修改时间读取与排序（图标不阻塞——先用通用字形秒出）
            List<ItemVm> vms;
            try
            {
                vms = found.Select(f => MakeFileVm(f.Path)).ToList();
            }
            catch { return; }
            if (token.IsCancellationRequested) return;
            // 按修改时间倒序（最新在前；时间字符串为 yyyy-MM-dd HH:mm，字典序即时间序）
            vms.Sort((a, b) => string.CompareOrdinal(b.TimeText, a.TimeText));

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (token.IsCancellationRequested) return;
                if (SearchBox.Text?.Trim() != query) return; // 用户已改词，丢弃旧结果
                _fileResults = vms;
                RebuildDisplay(SearchBox.Text?.Trim() ?? "", true);
                FillIconsAsync(vms, token);
            }));
        }, CancellationToken.None);
    }

    /// <summary>把文件搜索结果应用到列表（UI 线程调用；图标异步补充）。</summary>
    private void ApplyFileResults(List<(string Name, string Path)> found, string query)
    {
        if (SearchBox.Text?.Trim() != query) return; // 用户已改词，丢弃旧结果
        List<ItemVm> vms;
        try
        {
            vms = found.Select(f => MakeFileVm(f.Path)).ToList();
        }
        catch { return; }
        vms.Sort((a, b) => string.CompareOrdinal(b.TimeText, a.TimeText));
        _fileResults = vms;
        RebuildDisplay(SearchBox.Text?.Trim() ?? "", true);
        _ = Task.Run(() => FillIconsAsync(vms, CancellationToken.None));
    }

    /// <summary>后台逐个补充真实图标（提取完自动更新对应行）。</summary>
    private static void FillIconsAsync(List<ItemVm> vms, CancellationToken token)
    {
        foreach (var vm in vms)
        {
            if (token.IsCancellationRequested) return;
            try
            {
                var icon = IconProvider.GetFileIcon(vm.LaunchPath);
                if (icon != null) vm.Icon = icon; // INPC → 行内图标即时更新
            }
            catch { }
        }
    }

    /// <summary>构建一个文件搜索结果的视图模型（先不取图标——用通用字形秒出，图标由后台异步补充）。</summary>
    private static ItemVm MakeFileVm(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var bg = new SolidColorBrush(ItemVisuals.ColorFor("file"));
        bg.Freeze();
        string timeText = "";
        try
        {
            var t = System.IO.File.GetLastWriteTime(path);
            if (t.Year > 1601) timeText = t.ToString("yyyy-MM-dd HH:mm");
        }
        catch { }
        return new ItemVm
        {
            Source = null,
            Name = name,
            Subtitle = path,
            Icon = null, // 通用字形先显示，真实图标异步补
            Glyph = ItemVisuals.GlyphFor("file"),
            IconBg = bg,
            SearchText = (name + " " + path).ToLowerInvariant(),
            Kind = "file",
            LaunchPath = path,
            TimeText = timeText
        };
    }

    // ==================== 布局 ====================

    /// <summary>宫格瓷砖尺寸（需与 XAML 中 ListBoxItem 的 Width/Height 一致）。</summary>
    private const double TileWidth = 96;
    private const double TileHeight = 100;
    private const double ListRowHeight = 50;

    /// <summary>按配置切换条目排布：grid 宫格现状 / fill 宫格填充整行 / list 列表。</summary>
    private void ApplyLayoutMode()
    {
        var mode = _config.LayoutMode;
        ItemsPanelTemplate panel;
        if (mode == "list")
        {
            var factory = new FrameworkElementFactory(typeof(StackPanel));
            factory.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
            panel = new ItemsPanelTemplate(factory);
            ItemList.ItemContainerStyle = (Style)FindResource("ListContainerStyle");
            ItemList.ItemTemplate = (DataTemplate)FindResource("ListRowTemplate");
        }
        else if (mode == "fill")
        {
            panel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(FillWrapPanel)));
            ItemList.ItemContainerStyle = (Style)FindResource("TileContainerStyle");
            ItemList.ItemTemplate = (DataTemplate)FindResource("TileTemplate");
        }
        else
        {
            var factory = new FrameworkElementFactory(typeof(WrapPanel));
            factory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            panel = new ItemsPanelTemplate(factory);
            ItemList.ItemContainerStyle = (Style)FindResource("TileContainerStyle");
            ItemList.ItemTemplate = (DataTemplate)FindResource("TileTemplate");
        }
        ItemList.ItemsPanel = panel;
    }

    private void PositionWindow()
    {
        var wa = SystemParameters.WorkArea;
        var width = _config.WindowWidth > 400 ? _config.WindowWidth : 900;
        Width = Math.Clamp(width, 480, wa.Width - 40);

        var header = 58 + 1 + 34 + 14 + 12; // 搜索栏 + 分隔线 + 底部 + 边距
        double desired;
        if (_config.LayoutMode == "list")
        {
            desired = header + _filtered.Count * ListRowHeight + 8;
        }
        else
        {
            var cols = GridColumnCount();
            var rows = _filtered.Count == 0 ? 0 : Math.Max(1, (int)Math.Ceiling((double)_filtered.Count / cols));
            desired = header + rows * TileHeight + 8;
        }
        // 文件结果区：标题 + 行（每行 42px，最多展示 14 行，超出部分 FileList 内部滚动）
        if (_fileResults.Count > 0)
        {
            var fileRows = Math.Min(_fileResults.Count, 14);
            desired += 24 + fileRows * 42;
        }
        // 有文件结果时允许扩展到整个工作区高度（否则受 MaxWindowHeight 限制）
        var maxH = _fileResults.Count > 0
            ? Math.Max(wa.Height - 60, _config.MaxWindowHeight)
            : Math.Min(_config.MaxWindowHeight, wa.Height - 60);
        var newH = Math.Clamp(desired, 240, maxH);
        // 高度没变化就跳过设置，避免无谓的重排抖动
        if (Math.Abs(Height - newH) > 2) Height = newH;

        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + wa.Height * 0.18;
    }

    /// <summary>宫格每行列数（与 WrapPanel 实际换行一致）。用 Width 而非 ActualWidth，
    /// 因为首次显示时窗口尚未布局、ActualWidth 为 0。</summary>
    private int GridColumnCount() =>
        Math.Max(1, (int)((Width - 20) / TileWidth));

    // ==================== 交互 ====================

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => UpdateFilter();

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ((App)Application.Current).OpenSettings();
    }

    /// <summary>✨ AI 按钮 / “用 AI 回答”：把当前搜索内容交给 AI 对话窗口。</summary>
    private void OnAiClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var query = SearchBox.Text.Trim();
        ((App)Application.Current).OpenChat(query.Length > 0 ? query : null);
        HideMenu();
    }

    // ==================== 拖拽添加（拖到菜单上直接添加并保存） ====================

    private int _dragCounter;

    private void OnMenuDragEnter(object sender, DragEventArgs e)
    {
        _dragCounter++;
        OnMenuDragOver(sender, e);
    }

    private void OnMenuDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ItemDropHandler.IsAcceptable(e.Data)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        DropOverlay.Visibility = e.Effects == DragDropEffects.Copy
            ? Visibility.Visible
            : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnMenuDragLeave(object sender, DragEventArgs e)
    {
        if (_dragCounter > 0) _dragCounter--;
        if (_dragCounter == 0) DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnMenuDrop(object sender, DragEventArgs e)
    {
        _dragCounter = 0;
        DropOverlay.Visibility = Visibility.Collapsed;
        var items = ItemDropHandler.FromDrop(e.Data);
        if (items.Count > 0) AddItemsAndSave(items);
        e.Handled = true;
    }

    /// <summary>把拖拽得到的条目加入配置并立即保存生效。</summary>
    public void AddItemsAndSave(IEnumerable<DeskBuddyItem> newItems)
    {
        var added = newItems.ToList();
        if (added.Count == 0) return;
        _config.Items.AddRange(added);
        ((App)Application.Current).ApplyConfig(_config);
        RefreshItems();
        UpdateFilter();
        // 短暂提示后自动清除
        FooterHint.Text = $"已添加 {added.Count} 项";
        var clearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        clearTimer.Tick += (_, _) => { clearTimer.Stop(); FooterHint.Text = ""; };
        clearTimer.Start();
    }

    // ==================== MCP（AI 快捷添加菜单） ====================

    /// <summary>MCP：添加一个条目（校验 + 去重 + 保存 + 刷新），返回 JSON 结果。</summary>
    public string McpAddItem(DeskBuddyItem item)
    {
        var name = item.Name?.Trim() ?? "";
        var path = item.Path?.Trim() ?? "";
        var type = item.Type?.Trim().ToLowerInvariant() ?? "";
        if (name.Length == 0) return McpErr("名称不能为空");
        if (path.Length == 0) return McpErr("路径不能为空");
        if (type is not ("app" or "url" or "folder" or "file" or "command"))
            return McpErr($"不支持的条目类型：{item.Type}（可选 app/url/folder/file/command）");

        var cfg = ConfigManager.Load();
        if (cfg.Items.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
            return McpErr($"已存在同名条目「{name}」，如需替换请先 remove_menu_item 删除");

        var newItem = new DeskBuddyItem
        {
            Name = name,
            Type = type,
            Path = path,
            Args = item.Args ?? "",
            Keywords = item.Keywords ?? "",
            Icon = item.Icon ?? ""
        };
        cfg.Items.Add(newItem);
        ((App)Application.Current).ApplyConfig(cfg);
        _config = cfg;
        RefreshItems();
        UpdateFilter();
        return McpOk($"已添加「{name}」（{type}）", newItem);
    }

    /// <summary>MCP：按名称删除条目（大小写不敏感），返回 JSON 结果。</summary>
    public string McpRemoveItem(string name)
    {
        var cfg = ConfigManager.Load();
        var idx = cfg.Items.FindIndex(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return McpErr($"未找到条目「{name}」（可用 list_menu_items 查看现有条目）");
        var removed = cfg.Items[idx];
        cfg.Items.RemoveAt(idx);
        ((App)Application.Current).ApplyConfig(cfg);
        _config = cfg;
        RefreshItems();
        UpdateFilter();
        return McpOk($"已删除「{removed.Name}」", null);
    }

    /// <summary>MCP：列出所有条目，返回 JSON 结果。</summary>
    public string McpListItems()
    {
        var cfg = ConfigManager.Load();
        var items = cfg.Items.Select(i => new
        {
            name = i.Name,
            type = i.Type,
            path = i.Path,
            args = i.Args ?? "",
            keywords = i.Keywords ?? "",
            icon = i.Icon ?? "",
            hidden = i.Hidden
        }).ToList();
        return JsonSerializer.Serialize(new { ok = true, count = items.Count, items });
    }

    private static string McpOk(string message, DeskBuddyItem? item)
    {
        if (item == null) return JsonSerializer.Serialize(new { ok = true, message });
        return JsonSerializer.Serialize(new
        {
            ok = true,
            message,
            item = new { name = item.Name, type = item.Type, path = item.Path, args = item.Args ?? "", keywords = item.Keywords ?? "", icon = item.Icon ?? "" }
        });
    }

    private static string McpErr(string message) =>
        JsonSerializer.Serialize(new { ok = false, message });

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(0, 1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(0, -1);
                e.Handled = true;
                break;
            case Key.Left:
                MoveSelection(-1, 0);
                e.Handled = true;
                break;
            case Key.Right:
                MoveSelection(1, 0);
                e.Handled = true;
                break;
            case Key.PageDown:
                MoveSelection(0, 3);
                e.Handled = true;
                break;
            case Key.PageUp:
                MoveSelection(0, -3);
                e.Handled = true;
                break;
            case Key.Enter:
                LaunchSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                HideMenu();
                IndexProgressWindow.CloseIfActive(); // ESC 同时关闭右下角索引进度条
                e.Handled = true;
                break;
        }
    }

    /// <summary>宫格导航：dx 左右移动一格，dy 上下移动一行（行末不足时落到最后一个）。</summary>
    private void MoveSelection(int dx, int dy)
    {
        if (_filtered.Count == 0) return;
        var cols = GridColumnCount();
        var idx = ItemList.SelectedIndex;
        var target = dy != 0 ? idx + dy * cols : idx + dx;
        if (target >= _filtered.Count) target = _filtered.Count - 1;
        target = Math.Clamp(target, 0, _filtered.Count - 1);
        ItemList.SelectedIndex = target;
        ItemList.ScrollIntoView(ItemList.SelectedItem);
    }

    /// <summary>启动当前选中条目（文件结果则用默认程序打开；调试触发器也会调用）。</summary>
    public void LaunchSelected()
    {
        // 文件结果列表有选中项时优先启动文件
        if (FileList.SelectedItem is ItemVm fvm && fvm.Kind == "file")
        {
            _lastLaunchTime = DateTime.UtcNow;
            Launcher.OpenPath(fvm.LaunchPath);
            HideMenu();
            return;
        }
        if (ItemList.SelectedItem is not ItemVm vm) return;
        if (vm.Kind == "file-section") return; // 分区标题不可启动
        if (vm.Kind == "file")
        {
            _lastLaunchTime = DateTime.UtcNow;
            Launcher.OpenPath(vm.LaunchPath);
            HideMenu();
            return;
        }
        if (vm.Source is not { } src) return;
        _lastLaunchTime = DateTime.UtcNow;
        Launcher.Launch(src);
        HideMenu();
    }

    /// <summary>文件结果列表：单击直接启动（与菜单一致，单击即开）。</summary>
    private void OnFileListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var lbi = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (lbi?.DataContext is not ItemVm vm) return;
        if ((DateTime.UtcNow - _lastLaunchTime).TotalMilliseconds < 600) return; // 防双击重复
        FileList.SelectedItem = vm;
        ItemList.SelectedItem = null;
        _lastLaunchTime = DateTime.UtcNow;
        Launcher.OpenPath(vm.LaunchPath);
        HideMenu();
    }

    /// <summary>调试用：设置搜索框文字（QM_DEBUG=1 时由触发器调用）。</summary>
    public void SetSearchText(string text)
    {
        SearchBox.Text = text;
        SearchBox.CaretIndex = text.Length;
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging) return;
        // 单击已直接打开；双击的第二次触发在此拦截，避免重复启动
        if ((DateTime.UtcNow - _lastLaunchTime).TotalMilliseconds < 600) return;
        var lbi = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (lbi == null) return;
        if (ItemList.SelectedItem != lbi.DataContext) ItemList.SelectedItem = lbi.DataContext;
        LaunchSelected();
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // 拖拽换位：松手时交换位置
        if (_isDragging)
        {
            var lbi = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (_dragItem?.Source is { } dragSrc &&
                lbi?.DataContext is ItemVm target && target.Source is { } tgtSrc &&
                !ReferenceEquals(tgtSrc, dragSrc))
            {
                SwapItems(dragSrc, tgtSrc);
                SaveAndRefresh();
                ItemList.SelectedItem = target;
            }
            ResetDragState();
            e.Handled = true;
            return;
        }

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item == null) return;

        // 单击任意条目直接启动（统一单击打开，不再要求“已选中”或区分双击）
        if ((DateTime.UtcNow - _lastLaunchTime).TotalMilliseconds > 600)
        {
            if (ItemList.SelectedItem != item.DataContext) ItemList.SelectedItem = item.DataContext;
            LaunchSelected();
        }
    }

    // ==================== 图标右键菜单 / 拖拽换位 ====================

    private ItemVm? _contextItem;
    private DeskBuddyItem? _pendingDelete;

    private ItemVm? _dragItem;
    private Point _dragStart;
    private bool _isDragging;
    private ListBoxItem? _dragSourceContainer;

    private void OnRootPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 点击窗口其他位置 → 关闭右键菜单
        if (ItemMenuPopup.IsOpen) ItemMenuPopup.IsOpen = false;
        if (FileMenuPopup.IsOpen) FileMenuPopup.IsOpen = false;
    }

    private void OnListPreviewLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var lbi = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (lbi == null) return;
        // 文件搜索结果（无 Source）不支持拖拽换位
        if (lbi.DataContext is ItemVm { Kind: not "item" }) return;
        _dragItem = lbi.DataContext as ItemVm;
        _dragStart = e.GetPosition(this);
        _isDragging = false;
        _dragSourceContainer = lbi;
    }

    private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed || _isDragging) return;
        var pos = e.GetPosition(this);
        if ((pos - _dragStart).Length < 10) return;

        // 超过阈值 → 进入拖拽换位模式
        _isDragging = true;
        if (_dragSourceContainer != null) _dragSourceContainer.Opacity = 0.45;
    }

    private void OnListPreviewRightUp(object sender, MouseButtonEventArgs e)
    {
        var lbi = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (lbi == null) return;
        if (lbi.DataContext is not ItemVm vm) return;
        ItemList.SelectedItem = vm;
        if (vm.Kind == "file")
        {
            // 文件搜索结果：打开所在目录 / 复制路径
            _contextFilePath = vm.LaunchPath;
            FileMenuPopup.IsOpen = true;
            e.Handled = true;
            return;
        }
        if (vm.Kind != "item") return; // 分区头等不可操作
        _contextItem = vm;
        _pendingDelete = null;
        CtxDeleteText.Text = "删除";
        ItemMenuPopup.IsOpen = true;
        e.Handled = true;
    }

    private void ResetDragState()
    {
        if (_dragSourceContainer != null) _dragSourceContainer.Opacity = 1.0;
        _dragSourceContainer = null;
        _dragItem = null;
        _isDragging = false;
    }

    private void CloseItemMenu()
    {
        ItemMenuPopup.IsOpen = false;
        _pendingDelete = null;
        CtxDeleteText.Text = "删除";
    }

    /// <summary>全局 Esc 优先关闭右键菜单，返回是否已关闭（供 App 调用）。</summary>
    public bool CloseContextMenuIfOpen()
    {
        if (ItemMenuPopup.IsOpen)
        {
            CloseItemMenu();
            return true;
        }
        if (FileMenuPopup.IsOpen)
        {
            FileMenuPopup.IsOpen = false;
            return true;
        }
        return false;
    }

    // ==================== 文件搜索结果右键操作 ====================

    private string? _contextFilePath;
    private bool _fileDeleteConfirmed;

    /// <summary>在资源管理器中打开文件所在目录并选中该文件。</summary>
    private void OnOpenFileFolder(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.IsOpen = false;
        if (string.IsNullOrEmpty(_contextFilePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_contextFilePath}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>复制文件完整路径到剪贴板。</summary>
    private void OnCopyFilePath(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.IsOpen = false;
        if (string.IsNullOrEmpty(_contextFilePath)) return;
        try
        {
            System.Windows.Clipboard.SetText(_contextFilePath);
            ShowTransientHint("已复制文件路径");
        }
        catch { }
    }

    /// <summary>用默认程序打开该文件。</summary>
    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.IsOpen = false;
        if (string.IsNullOrEmpty(_contextFilePath)) return;
        _lastLaunchTime = DateTime.UtcNow;
        Launcher.OpenPath(_contextFilePath);
        HideMenu();
    }

    /// <summary>重命名文件（弹窗输入新名称）。</summary>
    private void OnRenameFile(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.IsOpen = false;
        _fileDeleteConfirmed = false;
        DeleteFileBtnText.Text = "删除";
        if (string.IsNullOrEmpty(_contextFilePath) || !System.IO.File.Exists(_contextFilePath)) return;
        var dir = System.IO.Path.GetDirectoryName(_contextFilePath) ?? "";
        var dlg = new RenameDialogWindow(System.IO.Path.GetFileName(_contextFilePath));
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.NewName)) return;
        var newName = dlg.NewName.Trim();
        if (newName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowTransientHint("文件名包含非法字符");
            return;
        }
        var newPath = System.IO.Path.Combine(dir, newName);
        if (string.Equals(newPath, _contextFilePath, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            System.IO.File.Move(_contextFilePath, newPath);
            // 更新当前显示的文件结果（索引由监听自动同步）
            for (var i = 0; i < _fileResults.Count; i++)
            {
                if (string.Equals(_fileResults[i].LaunchPath, _contextFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    _fileResults[i] = MakeFileVm(newPath);
                    break;
                }
            }
            RebuildDisplay(SearchBox.Text?.Trim() ?? "", true);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"重命名失败：{ex.Message}", "DeskBuddy");
        }
    }

    /// <summary>删除文件（两段式确认）。</summary>
    private void OnDeleteFile(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_contextFilePath)) return;
        if (!_fileDeleteConfirmed)
        {
            _fileDeleteConfirmed = true;
            DeleteFileBtnText.Text = "确认删除？";
            return;
        }
        _fileDeleteConfirmed = false;
        FileMenuPopup.IsOpen = false;
        try
        {
            System.IO.File.Delete(_contextFilePath);
            _fileResults.RemoveAll(v => string.Equals(v.LaunchPath, _contextFilePath, StringComparison.OrdinalIgnoreCase));
            RebuildDisplay(SearchBox.Text?.Trim() ?? "", true);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"删除失败：{ex.Message}", "DeskBuddy");
        }
    }

    private void ShowTransientHint(string text)
    {
        FooterHint.Text = text;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { t.Stop(); FooterHint.Text = ""; };
        t.Start();
    }

    private void OnCtxTop(object sender, RoutedEventArgs e) => MoveContextItemTo(0);
    private void OnCtxBottom(object sender, RoutedEventArgs e) => MoveContextItemTo(_config.Items.Count - 1);
    private void OnCtxUp(object sender, RoutedEventArgs e) => MoveContextItemBy(-1);
    private void OnCtxDown(object sender, RoutedEventArgs e) => MoveContextItemBy(1);

    private void MoveContextItemTo(int targetIndex)
    {
        if (_contextItem?.Source is not { } source) return;
        CloseItemMenu();
        var list = _config.Items;
        int idx = list.IndexOf(source);
        if (idx < 0 || idx == targetIndex) return;
        list.RemoveAt(idx);
        list.Insert(targetIndex, source);
        SaveAndRefresh();
        ItemList.SelectedIndex = Math.Min(targetIndex, _filtered.Count - 1);
    }

    private void MoveContextItemBy(int delta)
    {
        if (_contextItem?.Source is not { } source) return;
        CloseItemMenu();
        var list = _config.Items;
        int idx = list.IndexOf(source);
        if (idx < 0) return;
        int target = Math.Clamp(idx + delta, 0, list.Count - 1);
        if (target == idx) return;
        list.RemoveAt(idx);
        list.Insert(target, source);
        SaveAndRefresh();
        ItemList.SelectedIndex = Math.Min(target, _filtered.Count - 1);
    }

    private void OnCtxEdit(object sender, RoutedEventArgs e)
    {
        if (_contextItem?.Source is not { } source) return;
        CloseItemMenu();
        var editor = new ItemEditorWindow(source, _config.Theme);
        if (editor.ShowDialog() == true && editor.Item != null)
        {
            int idx = _config.Items.IndexOf(source);
            if (idx >= 0)
            {
                _config.Items[idx] = editor.Item;
                SaveAndRefresh();
                ItemList.SelectedIndex = Math.Min(idx, _filtered.Count - 1);
            }
        }
    }

    private void OnCtxDelete(object sender, RoutedEventArgs e)
    {
        if (_contextItem?.Source is not { } source) return;
        // 两段式确认，防止误删
        if (!ReferenceEquals(_pendingDelete, source))
        {
            _pendingDelete = source;
            CtxDeleteText.Text = "确认删除？";
            return;
        }
        CloseItemMenu();
        _config.Items.Remove(source);
        SaveAndRefresh();
        if (_filtered.Count > 0) ItemList.SelectedIndex = 0;
    }

    private void SwapItems(DeskBuddyItem a, DeskBuddyItem b)
    {
        var list = _config.Items;
        int ia = list.IndexOf(a), ib = list.IndexOf(b);
        if (ia < 0 || ib < 0 || ia == ib) return;
        (list[ia], list[ib]) = (list[ib], list[ia]);
        SaveAndRefresh();
    }

    private void SaveAndRefresh()
    {
        ((App)Application.Current).ApplyConfig(_config);
        RefreshItems();
        UpdateFilter();
    }

    /// <summary>调试触发器：对当前选中条目执行右键菜单动作（up/down/top/bottom/delete）。</summary>
    public void DebugCtxAction(string action)
    {
        if (ItemList.SelectedItem is not ItemVm vm) return;
        _contextItem = vm;
        switch (action)
        {
            case "up": MoveContextItemBy(-1); break;
            case "down": MoveContextItemBy(1); break;
            case "top": MoveContextItemTo(0); break;
            case "bottom": MoveContextItemTo(_config.Items.Count - 1); break;
            case "delete":
                _pendingDelete = vm.Source;
                OnCtxDelete(this, new RoutedEventArgs());
                break;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    // ==================== 失焦自动关闭 ====================

    private DateTime _shownTime;

    private void OnDeactivated(object sender, EventArgs e)
    {
        DebugLog.Write($"Deactivated (justOpened={_justOpened}, suppress={_suppressHide})");
        if (_justOpened || _suppressHide) return;
        StartHideTimer();
    }

    private void OnActivated(object sender, EventArgs e) { /* 监视器在窗口激活时不会隐藏，无需处理 */ }

    /// <summary>
    /// 显示期间持续监视：当前台不再是我们、鼠标不在窗口上、没有鼠标按键按住、
    /// 也没有子窗口打开时自动隐藏。不依赖 Deactivated 事件（该事件可能不触发）。
    /// </summary>
    private void StartHideTimer()
    {
        if (_hideTimer != null) return;
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _hideTimer.Tick += (_, _) => OnVisibilityTick();
        _hideTimer.Start();
    }

    private void OnVisibilityTick()
    {
        if (!IsVisible)
        {
            StopHideTimer();
            return;
        }
        if (ItemEditorWindow.IsOpen) return;                    // 编辑器打开时不关
        if (IsActive || IsMouseOver || IsAnyMouseButtonDown()) return; // 正在交互/拖拽
        if (IsOwnProcessForeground()) return;                   // 前台还是我们
        if ((DateTime.UtcNow - _shownTime).TotalMilliseconds < 1500) return; // 显示初期宽限

        StopHideTimer();
        DebugLog.Write("visibility monitor: auto-hiding");
        PerformHide();
    }

    /// <summary>当前前台窗口是否属于本进程（菜单还占着前台就不隐藏）。</summary>
    private bool IsOwnProcessForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out uint fgPid);
        return fgPid == (uint)Environment.ProcessId;
    }

    private static bool IsAnyMouseButtonDown() =>
        (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int VK_MBUTTON = 0x04;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private void StopHideTimer()
    {
        _hideTimer?.Stop();
        _hideTimer = null;
    }
}
