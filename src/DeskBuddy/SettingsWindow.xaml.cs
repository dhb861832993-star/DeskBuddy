using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskBuddy.Models;
using DeskBuddy.Services;

namespace DeskBuddy;

/// <summary>设置窗口中的一个菜单项行。</summary>
public sealed class ItemRow
{
    public required DeskBuddyItem Source { get; init; }
    public required string Name { get; init; }
    public required string Glyph { get; init; }
    public required Brush IconBg { get; init; }
    public required string Subtitle { get; init; }
}

/// <summary>设置窗口：快捷键捕获、双击间隔、主题、菜单项管理，保存后立即生效。</summary>
public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private string _selectedHotkey;
    private bool _capturing;
    private readonly ObservableCollection<ItemRow> _draftRows = new();

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        RoundedWindow.Apply(this, RootCard.CornerRadius.TopLeft);
        _selectedHotkey = NormalizeHotkey(config.Hotkey);
        HotkeyBtn.Content = MenuHotkeyDisplay(_selectedHotkey);
        PreviewKeyUp += OnPreviewKeyUp;   // 呼出捕获：修饰键松开（无主键跟来）= 录入单键双击
        IntervalSlider.Value = config.DoubleTapIntervalMs;
        UpdateIntervalLabel();

        switch (config.Theme)
        {
            case "dark": ThemeDark.IsChecked = true; break;
            case "light": ThemeLight.IsChecked = true; break;
            default: ThemeAuto.IsChecked = true; break;
        }

        switch (config.LayoutMode)
        {
            case "fill": LayoutFill.IsChecked = true; break;
            case "list": LayoutList.IsChecked = true; break;
            default: LayoutGrid.IsChecked = true; break;
        }

        foreach (var item in config.Items)
        {
            _draftRows.Add(MakeRow(item));
        }
        ItemList.ItemsSource = _draftRows;
        UpdateItemsHint();

        // AI 配置（全面基于本机 DeepSeek Harness）
        HarnessBaseUrlBox.Text = config.HarnessBaseUrl;
        HarnessSessionBox.Text = config.HarnessSessionId;
        ShowAllSessionsRow.IsChecked = config.HarnessShowAllSessions;
        UpdateAiHint();

        // MCP
        McpEnabledBox.IsChecked = config.McpEnabled;
        CopyMcpBtn.IsEnabled = config.McpEnabled;

        // 文件搜索
        EnableFileSearchBox.IsChecked = config.EnableFileSearch;
        SearchRootsBox.Text = string.Join("\n", config.SearchRoots);
        SearchBackendBox.SelectedIndex = config.FileSearchBackend switch
        {
            "usn" => 1,
            "wsearch" => 2,
            _ => 0
        };

        // 备忘录/工具箱（与主界面图标【同一状态源】：开关 = 面板是否显示，两边改都互相同步）
        MemoEnabledBox.IsChecked = config.MemoPanelOpen ?? config.MemoEnabled;
        ToolsEnabledBox.IsChecked = config.ToolsPanelOpen ?? config.ToolsEnabled;

        // 截图热键（组合键捕获式，格式 "Ctrl+Alt+S"）
        _snipHotkey = string.IsNullOrWhiteSpace(config.SnipHotkey) ? "F1" : config.SnipHotkey;
        SnipCaptureBtn.Content = _snipHotkey;
        SnipHotkeyHint.Text = $"当前：{_snipHotkey}（点击左侧按钮，然后直接按下任意组合键；Esc 取消）";

        SelectCategory("general"); // 默认显示「通用」

        Loaded += (_, _) => ApplyTheme();
    }

    /// <summary>复制 MCP 客户端接入配置（JSON）到剪贴板。</summary>
    private void OnCopyMcpConfig(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DeskBuddy.exe");
        var json = "{\n" +
                   "  \"mcpServers\": {\n" +
                   "    \"deskbuddy\": {\n" +
                   "      \"command\": \"" + exe.Replace("\\", "\\\\") + "\",\n" +
                   "      \"args\": [\"--mcp\"]\n" +
                   "    }\n" +
                   "  }\n" +
                   "}";
        try
        {
            Clipboard.SetText(json);
            McpHint.Text = "已复制接入配置 ✓";
        }
        catch
        {
            McpHint.Text = "复制失败，请手动配置：command = " + exe + "，args = [\"--mcp\"]";
        }
    }

    private void UpdateAiHint()
    {
        AiHint.Text = "AI 对话基于本机 DeepSeek Harness（零配置，数据不出电脑）。会话策略留空=最近会话，new=每次新建，或填 sessionId。";
    }

    // ==================== 左侧目录切换 ====================

    private void OnCategoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }) SelectCategory(tag);
    }

    /// <summary>切换到指定目录（general / ai / mcp / items），并更新左侧选中态。</summary>
    private void SelectCategory(string tag)
    {
        PanelGeneral.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        PanelSearch.Visibility = tag == "search" ? Visibility.Visible : Visibility.Collapsed;
        PanelAi.Visibility = tag == "ai" ? Visibility.Visible : Visibility.Collapsed;
        PanelMcp.Visibility = tag == "mcp" ? Visibility.Visible : Visibility.Collapsed;
        PanelItems.Visibility = tag == "items" ? Visibility.Visible : Visibility.Collapsed;
        SetCategoryState(CatGeneral, tag == "general");
        SetCategoryState(CatSearch, tag == "search");
        SetCategoryState(CatAi, tag == "ai");
        SetCategoryState(CatMcp, tag == "mcp");
        SetCategoryState(CatItems, tag == "items");
    }

    private void SetCategoryState(Button? btn, bool active)
    {
        if (btn == null) return;
        btn.Background = active ? (Brush)FindResource("SelectedBg") : Brushes.Transparent;
    }

    /// <summary>呼出热键规范化：旧修饰键映射回标准名；新格式（单键/组合键 Ctrl+Alt+Q）原样保留。</summary>
    private static string NormalizeHotkey(string? h)
    {
        h = (h ?? "").Trim();
        if (h.Length == 0) return "Alt";
        return h.ToLowerInvariant() switch
        {
            "alt" => "Alt",
            "shift" => "Shift",
            "capslock" => "CapsLock",
            "win" => "Win",
            "ctrl" => "Ctrl",
            _ => h   // 新式（F7 / Q / Ctrl+Alt+Q 组合）保留原样
        };
    }

    private static ItemRow MakeRow(DeskBuddyItem item)
    {
        var bg = new SolidColorBrush(ItemVisuals.ColorFor(item.Type));
        bg.Freeze();
        return new ItemRow
        {
            Source = item,
            Name = string.IsNullOrWhiteSpace(item.Name) ? item.Path : item.Name,
            Glyph = ItemVisuals.GlyphFor(item.Type),
            IconBg = bg,
            Subtitle = $"{ItemVisuals.TypeLabel(item.Type)} · {item.Path}"
        };
    }

    // ==================== 主题 / 亚克力 ====================

    private string SelectedTheme =>
        ThemeDark?.IsChecked == true ? "dark" :
        ThemeLight?.IsChecked == true ? "light" : "auto";

    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        // XAML 解析期间 RadioButton 尚未全部构造，此处可能被提前触发，做保护
        if (ThemeAuto == null || RootCard == null) return;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        var theme = Theme.From(SelectedTheme);
        Resources["TextPrimary"] = Frozen(theme.TextPrimary);
        Resources["TextSecondary"] = Frozen(theme.TextSecondary);
        Resources["HoverBg"] = Frozen(theme.HoverBg);
        Resources["SelectedBg"] = Frozen(theme.SelectedBg);
        Resources["CardBorder"] = Frozen(theme.BorderColor);
        Resources["SeparatorBrush"] = Frozen(theme.Separator);
        Resources["BtnBg"] = Frozen(theme.HoverBg);
        Resources["BtnBgHover"] = Frozen(Color.FromArgb(0x3D, theme.HoverBg.R, theme.HoverBg.G, theme.HoverBg.B));
        if (RootCard != null) RootCard.Background = new SolidColorBrush(theme.CardTint) { Opacity = theme.CardAlpha };
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ==================== 通用交互 ====================

    private bool _dragging;
    private Point _dragStart;
    private Point _winStart;
    private double _dpi = 1;

    /// <summary>按住窗口空白处（非控件区域）开始拖动（手动实现，兼容 AllowsTransparency 无边框窗口）。</summary>
    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 只有点击不在任何控件内（标题栏、空白处）时才拖动窗口；
        // 否则会把列表选中、按钮、滑块、主题切换等鼠标操作全部吞掉
        if (e.LeftButton != MouseButtonState.Pressed || IsInsideControl(e.OriginalSource as DependencyObject)) return;
        _dragging = true;
        _dragStart = NativeMouse.GetScreenPosition(); // 物理屏幕坐标：唯一参考系，避免拖拽回跳/抖动
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _winStart = new Point(Left, Top);
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var pos = NativeMouse.GetScreenPosition(); // 物理屏幕坐标
        // 位移阈值：小于 4px 视为点击抖动，不移动窗口（防止纯点击误触发拖拽）
        if (Math.Abs(pos.X - _dragStart.X) < 4 && Math.Abs(pos.Y - _dragStart.Y) < 4) return;
        Left = _winStart.X + (pos.X - _dragStart.X) / _dpi;
        Top = _winStart.Y + (pos.Y - _dragStart.Y) / _dpi;
    }

    private void OnCardMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private static bool IsInsideControl(DependencyObject? d)
    {
        while (d != null && d is not Window)
        {
            if (d is Control) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void OnDotClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: "close" }) Close();
    }

    // ===== 截图组合键捕获 =====
    private bool _snipCapturing;
    private string _snipHotkey = "F1";

    private void OnSnipCaptureClick(object sender, RoutedEventArgs e)
    {
        _snipCapturing = !_snipCapturing;
        if (_snipCapturing)
        {
            SnipCaptureBtn.Content = "请按下组合键…";
            SnipHotkeyHint.Text = "例如按住 Ctrl+Alt 再按 S（松手录入）；Esc 取消";
            SnipCaptureBtn.Focus();
        }
        else
        {
            SnipCaptureBtn.Content = _snipHotkey;
            SnipHotkeyHint.Text = "当前：" + _snipHotkey;
        }
    }

    /// <summary>当前按下的修饰键组合（Ctrl/Alt/Shift/Win，顺序固定）。</summary>
    private static string BuildModString()
    {
        var mods = new List<string>();
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) mods.Add("Ctrl");
        if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) mods.Add("Alt");
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) mods.Add("Shift");
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods.Add("Win");
        return string.Join("+", mods);
    }

    /// <summary>主键名（吃真实 Key——Alt 组合时 e.Key=System 真实键在 SystemKey，调用方已解包）。</summary>
    private static string? RealKeyName(Key k)
    {
        return k switch
        {
            >= Key.A and <= Key.Z => ((char)('A' + (k - Key.A))).ToString(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (int)(k - Key.NumPad0),
            >= Key.F1 and <= Key.F12 => "F" + (int)(k - Key.F1 + 1),
            Key.PrintScreen => "PrintScreen",
            Key.Space => "Space",
            Key.OemTilde => "`",
            Key.Escape => "Esc",
            _ => null
        };
    }

    /// <summary>主键名（从事件解包 SystemKey）。</summary>
    private static string? KeyName(KeyEventArgs e) => RealKeyName(e.Key == Key.System ? e.SystemKey : e.Key);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // ===== 截图组合键捕获 =====
        if (_snipCapturing)
        {
            e.Handled = true;
            if (e.Key == Key.Escape && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
            {
                _snipCapturing = false;
                SnipCaptureBtn.Content = _snipHotkey;
                SnipHotkeyHint.Text = "已取消。当前：" + _snipHotkey;
                return;
            }
            // 关键：按住 Alt 时其他键走 Key.System（真实键在 SystemKey）——
            // 必须先取真实键再判断是不是修饰键，否则 Alt+F1 的 F1 被当修饰键提示吞掉、永远录不进去
            var realKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if (realKey is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                    or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                var cur = BuildModString();
                SnipHotkeyHint.Text = $"已按：{cur} … 再按一个主键（字母/数字/F1-F12/` 等）";
                return;
            }
            // 主键：组合修饰键录入
            var mods = BuildModString();
            if (mods.Contains("Win")) { SnipHotkeyHint.Text = "Win 键暂不支持作截图修饰键，请用 Ctrl/Alt/Shift 组合"; return; }
            var main = RealKeyName(realKey);
            if (main == null) { SnipHotkeyHint.Text = $"“{realKey}”不支持，请按字母/数字/功能键"; return; }
            _snipHotkey = mods.Length > 0 ? $"{mods}+{main}" : main;
            _snipCapturing = false;
            SnipCaptureBtn.Content = _snipHotkey;
            SnipHotkeyHint.Text = $"已设置：{_snipHotkey}（保存后生效）";
            return;
        }

        if (!_capturing) return;
        e.Handled = true;

        // ===== 呼出热键组合捕获 =====
        if (e.Key == Key.Escape && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            _capturing = false; _menuModCandidate = null;
            HotkeyBtn.Content = MenuHotkeyDisplay(_selectedHotkey);
            HotkeyHint.Text = "已取消。当前：" + MenuHotkeyDisplay(_selectedHotkey);
            return;
        }
        // 按住 Alt 时其他键走 Key.System（真实键在 SystemKey）——先解包再判定
        var menuRealKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (e.Key is Key.System && menuRealKey == Key.Escape)
        {
            _menuModCandidate = null;   // Alt+Esc 释放录入状态，不取消
            return;
        }
        if (menuRealKey is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.CapsLock)
        {
            // 修饰键按下：记候选——【松开时无主键跟来 = 录入单键双击模式】
            _menuModCandidate = menuRealKey;
            var cur = BuildModString();
            HotkeyHint.Text = string.IsNullOrEmpty(cur)
                ? $"已按 {ModNameOf(menuRealKey)}——松开即录入「双击 {ModNameOf(menuRealKey)} 呼出」；若要组合键请继续按住并按主键"
                : $"已按：{cur} … 再按一个主键（字母/数字/F1-F12）";
            return;
        }
        _menuModCandidate = null;   // 有主键按下：候选作废（走组合/单键录入）
        var menuMods = BuildModString();
        var menuMain = RealKeyName(menuRealKey);
        if (menuMain == null || menuMain == "Esc")
        {
            HotkeyHint.Text = $"“{menuRealKey}”不支持，请按字母/数字/功能键或纯修饰键";
            return;
        }
        if (menuMods.Length == 0)
        {
            // 单键（任意字母/数字/功能键）→ 双击模式
            _selectedHotkey = menuMain;
            _capturing = false;
            HotkeyBtn.Content = MenuHotkeyDisplay(_selectedHotkey);
            HotkeyHint.Text = $"已设置：双击 {menuMain} 呼出菜单（保存后生效）";
            return;
        }
        if (menuMods.Contains("Win"))
        {
            HotkeyHint.Text = "Win 键暂不支持作呼出修饰键，请用 Ctrl/Alt/Shift 组合";
            return;
        }
        _selectedHotkey = $"{menuMods}+{menuMain}";
        _capturing = false;
        HotkeyBtn.Content = MenuHotkeyDisplay(_selectedHotkey);
        HotkeyHint.Text = $"已设置：按 {MenuHotkeyDisplay(_selectedHotkey)} 呼出菜单（保存后生效）";
    }

    private Key? _menuModCandidate;   // 呼出捕获中按下的修饰键（松开无主键=单键录入）

    /// <summary>修饰键 Key → 显示名。</summary>
    private static string ModNameOf(Key k) => k switch
    {
        Key.LeftCtrl or Key.RightCtrl => "Ctrl",
        Key.LeftAlt or Key.RightAlt => "Alt",
        Key.LeftShift or Key.RightShift => "Shift",
        Key.CapsLock => "CapsLock",
        Key.LWin or Key.RWin => "Win",
        _ => k.ToString()
    };

    /// <summary>呼出捕获中修饰键松开：期间没有主键 → 录入单修饰键（双击模式）。</summary>
    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        if (!_menuModCandidate.HasValue) return;
        var up = e.Key == Key.System ? e.SystemKey : e.Key;
        if (up != _menuModCandidate.Value) return;
        // 期间是否按过其他主键（修饰键仍按着的其他键不算）
        var othersDown = Keyboard.Modifiers & ~ModifierFor(up);
        if (othersDown != ModifierKeys.None)
        {
            _menuModCandidate = null;   // 有其他修饰键仍按住（多修饰组合场景）——不录入
            return;
        }
        _selectedHotkey = ModNameOf(up);
        _menuModCandidate = null;
        _capturing = false;
        HotkeyBtn.Content = MenuHotkeyDisplay(_selectedHotkey);
        HotkeyHint.Text = $"已设置：双击 {ModNameOf(up)} 呼出菜单（保存后生效）";
        e.Handled = true;
    }

    private static ModifierKeys ModifierFor(Key k) => k switch
    {
        Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
        Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
        Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
        _ => ModifierKeys.None
    };

    /// <summary>呼出热键展示名（单键 = 原名；组合 = 原串）。</summary>
    private static string MenuHotkeyDisplay(string hotkey)
    {
        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 1 ? $"〔双击 {parts[0]}〕" : hotkey;
    }

    private void OnHotkeyCaptureClick(object sender, RoutedEventArgs e)
    {
        _capturing = !_capturing;
        if (_capturing)
        {
            HotkeyBtn.Content = "请按键…";
            HotkeyHint.Text = "按住 Ctrl/Alt/Shift 再按主键（如 Ctrl+Alt+Q）= 按下即呼出；单按一个键（如 CapsLock/F7）= 双击呼出；Esc 取消";
            HotkeyBtn.Focus();
        }
        else
        {
            HotkeyBtn.Content = MenuHotkeyDisplay(_selectedHotkey);
            HotkeyHint.Text = "当前：" + MenuHotkeyDisplay(_selectedHotkey);
        }
    }

    /// <summary>全局 Esc：按键捕获中则取消捕获，否则关闭设置窗口。</summary>
    public void HandleGlobalEscape()
    {
        if (_snipCapturing)
        {
            _snipCapturing = false;
            SnipCaptureBtn.Content = _snipHotkey;
            SnipHotkeyHint.Text = "已取消。当前：" + _snipHotkey;
            return;
        }
        if (_capturing)
        {
            _capturing = false;
            HotkeyBtn.Content = MenuHotkeyDisplay(_selectedHotkey);
            HotkeyHint.Text = "已取消。当前：" + MenuHotkeyDisplay(_selectedHotkey);
            return;
        }
        Close();
    }

    private void OnIntervalChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateIntervalLabel();

    private void UpdateIntervalLabel() =>
        IntervalLabel.Text = $"两次按键间隔：{IntervalSlider.Value:0} 毫秒（越小越灵敏）";

    // ==================== 拖拽添加 ====================

    private int _dragCounter;

    private void OnItemsDragEnter(object sender, DragEventArgs e)
    {
        _dragCounter++;
        OnItemsDragOver(sender, e);
    }

    private void OnItemsDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ItemDropHandler.IsAcceptable(e.Data)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        DropHighlight.Visibility = e.Effects == DragDropEffects.Copy
            ? Visibility.Visible
            : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnItemsDragLeave(object sender, DragEventArgs e)
    {
        if (_dragCounter > 0) _dragCounter--;
        if (_dragCounter == 0) DropHighlight.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnItemsDrop(object sender, DragEventArgs e)
    {
        _dragCounter = 0;
        DropHighlight.Visibility = Visibility.Collapsed;
        var items = ItemDropHandler.FromDrop(e.Data);
        if (items.Count > 0) AddDraftItems(items);
        e.Handled = true;
    }

    /// <summary>把拖拽得到的条目加入草稿列表（点「保存」生效）。</summary>
    public void AddDraftItems(IEnumerable<DeskBuddyItem> items)
    {
        var list = items.ToList();
        foreach (var it in list) _draftRows.Add(MakeRow(it));
        if (_draftRows.Count > 0)
        {
            ItemList.SelectedIndex = _draftRows.Count - 1;
            ItemList.ScrollIntoView(ItemList.SelectedItem);
        }
        UpdateItemsHint();
        SaveHint.Text = list.Count > 0 ? $"已从桌面拖入 {list.Count} 项，点「保存」生效" : SaveHint.Text;
    }

    // ==================== 菜单项管理 ====================

    private void UpdateItemsHint() =>
        ItemsHint.Text = _draftRows.Count == 0
            ? "还没有菜单项，点「添加」创建"
            : $"共 {_draftRows.Count} 项 · 双击可编辑 · 点「保存」生效";

    private void OnItemsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DebugLog.Write($"items selection changed -> index {ItemList.SelectedIndex} count={_draftRows.Count}");
    }

    private void OnListPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        DebugLog.Write($"list mousedown at ({e.GetPosition(this)}) original={d?.GetType().Name} inItem={FindAncestor<ListBoxItem>(d) != null}");
    }

    private void OnListPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        DebugLog.Write($"list mouseup at ({e.GetPosition(this)}) original={d?.GetType().Name} inItem={FindAncestor<ListBoxItem>(d) != null} selectedIndex={ItemList.SelectedIndex}");
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private void OnAddItem(object sender, RoutedEventArgs e)
    {
        var editor = new ItemEditorWindow(null, SelectedTheme);
        if (editor.ShowDialog() == true && editor.Item != null)
        {
            _draftRows.Add(MakeRow(editor.Item));
            ItemList.SelectedIndex = _draftRows.Count - 1;
            ItemList.ScrollIntoView(ItemList.SelectedItem);
            UpdateItemsHint();
        }
    }

    private void OnEditItem(object sender, RoutedEventArgs e)
    {
        var idx = ItemList.SelectedIndex;
        if (idx < 0 || idx >= _draftRows.Count) return;
        var row = _draftRows[idx];
        var editor = new ItemEditorWindow(row.Source, SelectedTheme);
        if (editor.ShowDialog() == true && editor.Item != null)
        {
            _draftRows[idx] = MakeRow(editor.Item);
            ItemList.SelectedIndex = idx;
            UpdateItemsHint();
        }
    }

    private void OnDeleteItem(object sender, RoutedEventArgs e)
    {
        var idx = ItemList.SelectedIndex;
        DebugLog.Write($"delete clicked: selectedIndex={idx} count={_draftRows.Count}");
        if (idx < 0 || idx >= _draftRows.Count) return;
        _draftRows.RemoveAt(idx);
        if (_draftRows.Count > 0)
        {
            ItemList.SelectedIndex = Math.Min(idx, _draftRows.Count - 1);
        }
        UpdateItemsHint();
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OnEditItem(sender, e);
    }

    private void OnMoveUp(object sender, RoutedEventArgs e)
    {
        var idx = ItemList.SelectedIndex;
        DebugLog.Write($"moveup clicked: selectedIndex={idx}");
        if (idx <= 0) return;
        _draftRows.Move(idx, idx - 1);
        ItemList.SelectedIndex = idx - 1;
    }

    private void OnMoveDown(object sender, RoutedEventArgs e)
    {
        var idx = ItemList.SelectedIndex;
        DebugLog.Write($"movedown clicked: selectedIndex={idx}");
        if (idx < 0 || idx >= _draftRows.Count - 1) return;
        _draftRows.Move(idx, idx + 1);
        ItemList.SelectedIndex = idx + 1;
    }

    // ==================== 保存 / 取消 ====================

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnClearMemoArchive(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this, "确定清空所有已完成备忘录吗？此操作不可撤销。", "清空已完成备忘录", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        var memoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "DeskBuddy.memo.json");
        try
        {
            if (!System.IO.File.Exists(memoPath)) return;
            var json = System.IO.File.ReadAllText(memoPath);
            var list = System.Text.Json.JsonSerializer.Deserialize<List<MemoItem>>(json) ?? new();
            var pending = list.Where(x => !x.Done).ToList();
            System.IO.File.Copy(memoPath, memoPath + ".bak", true);
            System.IO.File.WriteAllText(memoPath, System.Text.Json.JsonSerializer.Serialize(pending));
            MessageBox.Show(this, "已清空已完成备忘录。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, "清空失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var cfg = new AppConfig
        {
            Hotkey = _selectedHotkey,
            DoubleTapIntervalMs = (int)IntervalSlider.Value,
            Theme = SelectedTheme,
            LayoutMode = LayoutFill?.IsChecked == true ? "fill" : LayoutList?.IsChecked == true ? "list" : "grid",
            WindowWidth = _config.WindowWidth,
            MaxWindowHeight = _config.MaxWindowHeight,
            Items = _draftRows.Select(r => r.Source).ToList(),
            AiMode = "harness", // 全面基于本机 DeepSeek Harness
            HarnessBaseUrl = HarnessBaseUrlBox.Text.Trim(),
            HarnessSessionId = HarnessSessionBox.Text.Trim(),
            HarnessShowAllSessions = ShowAllSessionsRow.IsChecked == true,
            AiBaseUrl = _config.AiBaseUrl,
            AiModel = _config.AiModel,
            AiApiKey = _config.AiApiKey,
            AiSystemPrompt = _config.AiSystemPrompt,
            McpEnabled = McpEnabledBox.IsChecked == true,
            // 统一状态源：设置勾选 = 面板显示状态（总开关与记忆合一，两边互相同步）
            MemoEnabled = MemoEnabledBox.IsChecked == true,
            MemoPanelOpen = MemoEnabledBox.IsChecked == true,
            ToolsEnabled = ToolsEnabledBox.IsChecked == true,
            ToolsPanelOpen = ToolsEnabledBox.IsChecked == true,
            SnipHotkey = _snipHotkey,
            EnableFileSearch = EnableFileSearchBox.IsChecked == true,
            SearchRoots = SearchRootsBox.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            FileSearchBackend = (SearchBackendBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "auto"
        };
        ((App)Application.Current).ApplyConfig(cfg);
        Close();
    }
}
