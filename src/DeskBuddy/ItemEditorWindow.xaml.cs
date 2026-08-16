using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskBuddy.Models;
using DeskBuddy.Services;

namespace DeskBuddy;

/// <summary>菜单项编辑对话框（添加 / 编辑）。保存后通过 Item 属性取回结果。</summary>
public partial class ItemEditorWindow : Window
{
    private static readonly string[] Types = { "app", "url", "folder", "file", "command" };
    private static readonly string[] TypeLabels = { "程序 (app)", "网页 (url)", "文件夹 (folder)", "文件 (file)", "命令 (command)" };

    private readonly string _themeName;

    /// <summary>编辑结果；取消时为 null。</summary>
    public DeskBuddyItem? Item { get; private set; }

    /// <summary>是否有编辑窗口打开（App 据此忽略双击热键，避免误触）。</summary>
    public static bool IsOpen { get; private set; }

    /// <summary>当前打开的编辑器实例（供全局 Esc 关闭）。</summary>
    public static ItemEditorWindow? Current { get; private set; }

    public ItemEditorWindow(DeskBuddyItem? existing, string themeName)
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

        if (existing != null)
        {
            Title = "编辑菜单项";
            TitleText.Text = "编辑菜单项";
            NameBox.Text = existing.Name;
            TypeCombo.SelectedIndex = System.Math.Max(0, System.Array.IndexOf(Types, existing.Type));
            PathBox.Text = existing.Path;
            ArgsBox.Text = existing.Args;
            KeywordBox.Text = existing.Keywords;
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

    private bool _dragging;
    private Point _dragStart;
    private Point _winStart;
    private double _dpi = 1;

    /// <summary>按住窗口空白处（非控件区域）开始拖动（手动实现，兼容 AllowsTransparency 无边框窗口）。</summary>
    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 只有点击不在任何控件内时才拖动窗口，避免吞掉输入框/下拉框/按钮的鼠标操作
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

        Item = new DeskBuddyItem
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? path : NameBox.Text.Trim(),
            Type = SelectedType,
            Path = path,
            Args = ArgsBox.Text.Trim(),
            Keywords = KeywordBox.Text.Trim()
        };
        DialogResult = true; // ShowDialog 返回 true 并自动关闭
    }
}
