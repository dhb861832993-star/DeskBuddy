using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DeskBuddy.Services;

namespace DeskBuddy;

/// <summary>重命名对话框：返回输入的新名称（不含路径）。</summary>
public partial class RenameDialogWindow : Window
{
    /// <summary>确认后输入的新文件名；取消为 null。</summary>
    public string? NewName { get; private set; }

    public RenameDialogWindow(string currentName)
    {
        InitializeComponent();
        RoundedWindow.Apply(this, RootCard.CornerRadius.TopLeft);
        NameBox.Text = currentName;
        NameBox.SelectAll();
        Loaded += (_, _) =>
        {
            ApplyTheme();
            NameBox.Focus();
        };
    }

    private void ApplyTheme()
    {
        var theme = Theme.From(((App)Application.Current).CurrentConfig.Theme);
        Resources["TextPrimary"] = Frozen(theme.TextPrimary);
        Resources["TextSecondary"] = Frozen(theme.TextSecondary);
        Resources["HoverBg"] = Frozen(theme.HoverBg);
        Resources["SelectedBg"] = Frozen(theme.SelectedBg);
        Resources["CardBorder"] = Frozen(theme.BorderColor);
        Resources["BtnBg"] = Frozen(theme.HoverBg);
        Resources["BtnBgHover"] = Frozen(Color.FromArgb(0x3D, theme.HoverBg.R, theme.HoverBg.G, theme.HoverBg.B));
        RootCard.Background = new SolidColorBrush(theme.CardTint) { Opacity = theme.CardAlpha };
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var v = NameBox.Text.Trim();
        if (v.Length == 0) return;
        NewName = v;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnOk(sender, e);
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
}
