using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickMenu.Models;
using QuickMenu.Services;

namespace QuickMenu;

/// <summary>菜单项编辑对话框（添加 / 编辑）。保存后通过 Item 属性取回结果。</summary>
public partial class ItemEditorWindow : Window
{
    private static readonly string[] Types = { "app", "url", "folder", "file", "command" };
    private static readonly string[] TypeLabels = { "程序 (app)", "网页 (url)", "文件夹 (folder)", "文件 (file)", "命令 (command)" };

    private readonly string _themeName;

    /// <summary>编辑结果；取消时为 null。</summary>
    public QuickMenuItem? Item { get; private set; }

    /// <summary>是否有编辑窗口打开（App 据此忽略双击热键，避免误触）。</summary>
    public static bool IsOpen { get; private set; }

    /// <summary>当前打开的编辑器实例（供全局 Esc 关闭）。</summary>
    public static ItemEditorWindow? Current { get; private set; }

    public ItemEditorWindow(QuickMenuItem? existing, string themeName)
    {
        InitializeComponent();
        _themeName = themeName;
        RoundedWindow.Apply(this, RootCard.CornerRadius.TopLeft);
        IsOpen = true;
        Current = this;
        Closed += (_, _) =>
        {
            IsOpen = false;
            if (ReferenceEquals(Current, this)) Current = null;
        };

        TypeCombo.ItemsSource = TypeLabels;

        // 已有文件夹列表（供下拉选择；编辑框可输入新文件夹名）
        var existingFolders = ((App)Application.Current).CurrentConfig.Items
            .Select(i => i.Folder)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        FolderCombo.ItemsSource = existingFolders;

        if (existing != null)
        {
            Title = "编辑菜单项";
            TitleText.Text = "编辑菜单项";
            NameBox.Text = existing.Name;
            TypeCombo.SelectedIndex = System.Math.Max(0, System.Array.IndexOf(Types, existing.Type));
            PathBox.Text = existing.Path;
            ArgsBox.Text = existing.Args;
            KeywordBox.Text = existing.Keywords;
            FolderCombo.Text = existing.Folder;
        }
        else
        {
            Title = "添加菜单项";
            TitleText.Text = "添加菜单项";
            TypeCombo.SelectedIndex = 0;
        }

        UpdatePathLabel();

        Loaded += (_, _) => { ApplyTheme(); NameBox.Focus(); };
    }

    // ==================== 主题 ====================

    private void ApplyTheme()
    {
        var theme = Theme.From(_themeName);
        Resources["TextPrimary"] = Frozen(theme.TextPrimary);
        Resources["TextSecondary"] = Frozen(theme.TextSecondary);
        Resources["HoverBg"] = Frozen(theme.HoverBg);
        Resources["SelectedBg"] = Frozen(theme.SelectedBg);
        Resources["CardBorder"] = Frozen(theme.BorderColor);
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

    // ==================== 交互 ====================

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 只有点击不在任何控件内时才拖动窗口，避免吞掉输入框/下拉框/按钮的鼠标操作
        if (e.LeftButton == MouseButtonState.Pressed && !IsInsideControl(e.OriginalSource as DependencyObject))
        {
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

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e) => UpdatePathLabel();

    private void UpdatePathLabel() =>
        PathLabel.Text = ItemVisuals.PathLabel(SelectedType);

    private string SelectedType =>
        TypeCombo.SelectedIndex >= 0 && TypeCombo.SelectedIndex < Types.Length
            ? Types[TypeCombo.SelectedIndex]
            : "app";

    private void OnPathEnter(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnSave(sender, e);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            Hint.Text = "请填写程序名、路径或网址。";
            PathBox.Focus();
            return;
        }
        if (SelectedType == "url" && !Uri.TryCreate(path, UriKind.Absolute, out _))
        {
            Hint.Text = "网址格式不正确，请包含 https:// 或 http://。";
            PathBox.Focus();
            return;
        }

        Item = new QuickMenuItem
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? path : NameBox.Text.Trim(),
            Type = SelectedType,
            Path = path,
            Args = ArgsBox.Text.Trim(),
            Keywords = KeywordBox.Text.Trim(),
            Folder = FolderCombo.Text.Trim()
        };
        DialogResult = true; // ShowDialog 返回 true 并自动关闭
    }
}
