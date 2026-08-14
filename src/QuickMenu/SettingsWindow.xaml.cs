using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickMenu.Models;
using QuickMenu.Services;

namespace QuickMenu;

/// <summary>设置窗口中的一个菜单项行。</summary>
public sealed class ItemRow
{
    public required QuickMenuItem Source { get; init; }
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
        HotkeyBtn.Content = _selectedHotkey;
        IntervalSlider.Value = config.DoubleTapIntervalMs;
        UpdateIntervalLabel();

        switch (config.Theme)
        {
            case "dark": ThemeDark.IsChecked = true; break;
            case "light": ThemeLight.IsChecked = true; break;
            default: ThemeAuto.IsChecked = true; break;
        }

        foreach (var item in config.Items)
        {
            _draftRows.Add(MakeRow(item));
        }
        ItemList.ItemsSource = _draftRows;
        UpdateItemsHint();

        // AI 配置
        ModeHarness.IsChecked = config.AiMode != "openai";
        ModeOpenAi.IsChecked = config.AiMode == "openai";
        HarnessBaseUrlBox.Text = config.HarnessBaseUrl;
        HarnessSessionBox.Text = config.HarnessSessionId;
        AiBaseUrlBox.Text = config.AiBaseUrl;
        AiModelBox.Text = config.AiModel;
        AiKeyBox.Text = config.AiApiKey;
        AiPromptBox.Text = config.AiSystemPrompt;
        UpdateAiHint();

        Loaded += (_, _) => ApplyTheme();
    }

    private void OnAiModeChanged(object sender, RoutedEventArgs e) => UpdateAiHint();

    private void UpdateAiHint()
    {
        if (ModeHarness?.IsChecked == true)
        {
            AiHint.Text = "本机 Harness 模式：直接和本机运行的 DeepSeek Harness 对话（零配置）。会话策略留空=最近会话，new=每次新建，或填 sessionId。";
        }
        else
        {
            AiHint.Text = "OpenAI 兼容模式：需在下面填写 API 密钥（DeepSeek 开放平台申请）。";
        }
    }

    private static string NormalizeHotkey(string h) => h?.ToUpperInvariant() switch
    {
        "ALT" => "Alt",
        "SHIFT" => "Shift",
        "CAPSLOCK" => "CapsLock",
        "WIN" => "Win",
        _ => "Ctrl"
    };

    private static ItemRow MakeRow(QuickMenuItem item)
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

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 只有点击不在任何控件内（标题栏、空白处）时才拖动窗口；
        // 否则会把列表选中、按钮、滑块、主题切换等鼠标操作全部吞掉
        if (e.LeftButton == MouseButtonState.Pressed && !IsInsideControl(e.OriginalSource as DependencyObject))
        {
            DebugLog.Write($"DragMove from {e.OriginalSource?.GetType().Name}");
            DragMove();
        }
    }

    private static bool IsInsideControl(DependencyObject? d)
    {
        while (d != null)
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

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;

        var key = e.Key switch
        {
            Key.LeftCtrl or Key.RightCtrl => "Ctrl",
            Key.LeftAlt or Key.RightAlt or Key.System => "Alt",
            Key.LeftShift or Key.RightShift => "Shift",
            Key.CapsLock => "CapsLock",
            Key.LWin or Key.RWin => "Win",
            _ => null
        };

        if (e.Key == Key.Escape)
        {
            _capturing = false;
            HotkeyBtn.Content = _selectedHotkey;
            HotkeyHint.Text = "已取消。点击右侧按钮后，再直接按下新快捷键";
            return;
        }

        if (key == null)
        {
            HotkeyHint.Text = $"“{e.Key}”不受支持，请按 Ctrl / Alt / Shift / CapsLock / Win";
            return;
        }

        _selectedHotkey = key;
        _capturing = false;
        HotkeyBtn.Content = key;
        HotkeyHint.Text = $"已选择：双击 {key} 呼出菜单（保存后生效）";
    }

    private void OnHotkeyCaptureClick(object sender, RoutedEventArgs e)
    {
        _capturing = !_capturing;
        if (_capturing)
        {
            HotkeyBtn.Content = "请按键…";
            HotkeyHint.Text = "请按下新的快捷键（仅支持 Ctrl / Alt / Shift / CapsLock / Win），按 Esc 取消";
        }
        else
        {
            HotkeyBtn.Content = _selectedHotkey;
            HotkeyHint.Text = "点击右侧按钮，再直接按下 Ctrl / Alt / Shift / CapsLock / Win";
        }
    }

    /// <summary>全局 Esc：按键捕获中则取消捕获，否则关闭设置窗口。</summary>
    public void HandleGlobalEscape()
    {
        if (_capturing)
        {
            _capturing = false;
            HotkeyBtn.Content = _selectedHotkey;
            HotkeyHint.Text = "已取消。点击右侧按钮后，再直接按下新快捷键";
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
    public void AddDraftItems(IEnumerable<QuickMenuItem> items)
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

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var cfg = new AppConfig
        {
            Hotkey = _selectedHotkey,
            DoubleTapIntervalMs = (int)IntervalSlider.Value,
            Theme = SelectedTheme,
            WindowWidth = _config.WindowWidth,
            MaxWindowHeight = _config.MaxWindowHeight,
            Items = _draftRows.Select(r => r.Source).ToList(),
            AiMode = ModeOpenAi?.IsChecked == true ? "openai" : "harness",
            HarnessBaseUrl = HarnessBaseUrlBox.Text.Trim(),
            HarnessSessionId = HarnessSessionBox.Text.Trim(),
            AiBaseUrl = AiBaseUrlBox.Text.Trim(),
            AiModel = AiModelBox.Text.Trim(),
            AiApiKey = AiKeyBox.Text.Trim(),
            AiSystemPrompt = AiPromptBox.Text.Trim()
        };
        ((App)Application.Current).ApplyConfig(cfg);
        Close();
    }
}
