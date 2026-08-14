using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickMenu.Services;

namespace QuickMenu;

/// <summary>对话中的一条消息（含流式追加支持）。</summary>
public sealed class ChatItem : INotifyPropertyChanged
{
    public string Role { get; init; } = "assistant"; // user | assistant
    private string _text = "";

    public string Text
    {
        get => _text;
        set { _text = value; OnPropertyChanged(); }
    }

    public HorizontalAlignment Align => Role == "user" ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public Brush BubbleBg => Role == "user"
        ? new SolidColorBrush(Color.FromRgb(0x2A, 0x6D, 0xF4))
        : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

    public Brush TextColor => Role == "user"
        ? Brushes.White
        : new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7));

    public void Append(string s)
    {
        _text += s;
        OnPropertyChanged(nameof(Text));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>AI 对话窗口：流式回复、多轮上下文、停止/清空。</summary>
public partial class ChatWindow : Window
{
    private readonly ObservableCollection<ChatItem> _messages = new();
    private readonly List<(string Role, string Content)> _history = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    public ChatWindow()
    {
        InitializeComponent();
        MsgList.ItemsSource = _messages;
        RoundedWindow.Apply(this, RootCard.CornerRadius.TopLeft);
        Loaded += (_, _) => ApplyTheme();
    }

    // ==================== 主题 ====================

    private void ApplyTheme()
    {
        var theme = Theme.From(((App)Application.Current).CurrentConfig.Theme);
        Resources["TextPrimary"] = Frozen(theme.TextPrimary);
        Resources["TextSecondary"] = Frozen(theme.TextSecondary);
        Resources["HoverBg"] = Frozen(theme.HoverBg);
        Resources["SelectedBg"] = Frozen(theme.SelectedBg);
        Resources["CardBorder"] = Frozen(theme.BorderColor);
        Resources["SeparatorBrush"] = Frozen(theme.Separator);
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

    // ==================== 对外接口 ====================

    /// <summary>显示窗口；initial 非空时直接发出提问。</summary>
    public void ShowAndAsk(string? initial)
    {
        var cfg = ((App)Application.Current).CurrentConfig;
        ModelText.Text = cfg.AiModel;
        TitleText.Text = cfg.AiEnabled ? "AI 助手" : "AI 助手（未配置密钥）";

        Show();
        Activate();

        if (!string.IsNullOrWhiteSpace(initial))
        {
            InputBox.Text = initial;
            Send();
        }
        else
        {
            InputBox.Focus();
        }
    }

    // ==================== 发送 / 停止 ====================

    private void OnSend(object sender, RoutedEventArgs e) => Send();

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            Send();
        }
    }

    private void Send()
    {
        if (_busy) return;
        var text = InputBox.Text.Trim();
        if (text.Length == 0) return;
        InputBox.Clear();

        var cfg = ((App)Application.Current).CurrentConfig;
        if (!cfg.AiEnabled)
        {
            AppendMessage("user", text);
            AppendMessage("assistant", "⚠️ AI 未就绪。\n本机 Harness 模式需在本机运行 DeepSeek Harness（127.0.0.1:3080）；OpenAI 模式需在 设置 → AI 对话 填写 API 密钥。");
            return;
        }

        AppendMessage("user", text);
        if (cfg.AiMode != "harness")
        {
            _history.Add(("user", text));
        }

        var assistant = AppendMessage("assistant", "");
        _busy = true;
        SendBtn.IsEnabled = false;
        StopBtn.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();

        var progress = new Progress<string>(s => Dispatcher.Invoke(() => { assistant.Append(s); ScrollToEnd(); }));

        _ = Task.Run(async () =>
        {
            try
            {
                if (cfg.AiMode == "harness")
                {
                    await HarnessClient.AskAsync(cfg, text, progress, _cts.Token);
                }
                else
                {
                    await AiClient.AskAsync(cfg, _history, text, progress, _cts.Token);
                    Dispatcher.Invoke(() => _history.Add(("assistant", assistant.Text)));
                }
            }
            catch (OperationCanceledException)
            {
                if (cfg.AiMode != "harness") Dispatcher.Invoke(() => _history.Add(("assistant", assistant.Text)));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    if (assistant.Text.Length == 0) assistant.Text = $"⚠️ {ex.Message}";
                    else assistant.Append($"\n\n⚠️ {ex.Message}");
                });
            }
            finally
            {
                Dispatcher.Invoke(ResetBusy);
            }
        }, _cts.Token);
    }

    private void ResetBusy()
    {
        _busy = false;
        SendBtn.IsEnabled = true;
        StopBtn.Visibility = Visibility.Collapsed;
        InputBox.Focus();
        ScrollToEnd();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        var cfg = ((App)Application.Current).CurrentConfig;
        if (cfg.AiMode == "harness")
        {
            _ = Task.Run(async () => { try { await HarnessClient.CancelAsync(cfg, CancellationToken.None); } catch { } });
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _messages.Clear();
        _history.Clear();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();

    private ChatItem AppendMessage(string role, string text)
    {
        var item = new ChatItem { Role = role, Text = text };
        _messages.Add(item);
        ScrollToEnd();
        return item;
    }

    private void ScrollToEnd() => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
    {
        MsgScroll.ScrollToEnd();
    }));

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Hide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        base.OnClosed(e);
    }
}
