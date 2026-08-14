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

/// <summary>对话中的一条消息（kind: user / assistant / status）。</summary>
public sealed class ChatItem : INotifyPropertyChanged
{
    public string Kind { get; init; } = "assistant"; // user | assistant | status
    private string _text = "";

    public string Text
    {
        get => _text;
        set { _text = value; OnPropertyChanged(); }
    }

    public HorizontalAlignment Align => Kind == "user" ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public Brush BubbleBg => Kind switch
    {
        "user" => new SolidColorBrush(Color.FromRgb(0x2A, 0x6D, 0xF4)),
        "assistant" => new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
        _ => Brushes.Transparent
    };

    public Brush TextColor => Kind == "user"
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

/// <summary>AI 对话窗口：本机 Harness 模式（会话管理、状态流、授权/提问）与 OpenAI 模式。</summary>
public partial class ChatWindow : Window, IHarnessObserver
{
    private readonly ObservableCollection<ChatItem> _messages = new();
    private readonly List<(string Role, string Content)> _openaiHistory = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    private readonly List<HarnessSession> _sessions = new();
    private string _currentSessionId = "";
    private bool _sessionSwitching;
    private HarnessApproval? _pendingApproval;
    private HarnessQuestion? _pendingQuestion;
    private string _lastStatus = "";
    private bool _thinkingShown;

    private bool IsHarness => ((App)Application.Current).CurrentConfig.AiMode == "harness";

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

    public void ShowAndAsk(string? initial)
    {
        var cfg = ((App)Application.Current).CurrentConfig;
        ModelText.Text = IsHarness ? "本机 Harness" : cfg.AiModel;

        SessionCombo.Visibility = IsHarness ? Visibility.Visible : Visibility.Collapsed;
        RefreshSessionsBtn.Visibility = SessionCombo.Visibility;

        Show();
        Activate();

        if (IsHarness)
        {
            _ = LoadSessionsAsync(initial);
        }
        else if (!string.IsNullOrWhiteSpace(initial))
        {
            InputBox.Text = initial;
            Send();
        }
        else
        {
            InputBox.Focus();
        }
    }

    // ==================== 会话管理 ====================

    private async Task LoadSessionsAsync(string? initial)
    {
        try
        {
            var cfg = ((App)Application.Current).CurrentConfig;
            _sessions.Clear();
            _sessions.AddRange(await HarnessClient.ListSessionsAsync(cfg, CancellationToken.None));
            SessionCombo.ItemsSource = _sessions.Select(s => s.Display).ToList();
            DebugLog.Write("sessions: " + string.Join(" | ", _sessions.Select(s => $"{s.Display} ({s.SessionId[..8]})")));

            var target = await HarnessClient.ResolveSessionIdAsync(cfg, CancellationToken.None);
            _currentSessionId = target;
            var idx = _sessions.FindIndex(s => s.SessionId == target);
            if (idx >= 0)
            {
                _sessionSwitching = true;
                SessionCombo.SelectedIndex = idx;
                _sessionSwitching = false;
            }
            await LoadHistoryAsync();
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
        catch (Exception ex)
        {
            AppendMessage("status", $"⚠️ 无法连接本机 Harness：{ex.Message}");
        }
    }

    private void OnSessionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sessionSwitching || SessionCombo.SelectedIndex < 0) return;
        if (SessionCombo.SelectedIndex < _sessions.Count)
        {
            _currentSessionId = _sessions[SessionCombo.SelectedIndex].SessionId;
            _ = LoadHistoryAsync();
        }
    }

    private void OnRefreshSessions(object sender, RoutedEventArgs e) => _ = LoadSessionsAsync(null);

    private async Task LoadHistoryAsync()
    {
        try
        {
            var cfg = ((App)Application.Current).CurrentConfig;
            var rows = await HarnessClient.GetHistoryAsync(cfg, _currentSessionId, CancellationToken.None);
            _messages.Clear();
            foreach (var (role, text) in rows)
            {
                AppendMessage(role, text);
            }
            ScrollToEnd();
        }
        catch { }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _messages.Clear();
        _openaiHistory.Clear();
        ActionBar.Visibility = Visibility.Collapsed;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();

    // ==================== 发送 ====================

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
            AppendMessage("assistant", "⚠️ AI 未就绪。本机 Harness 模式需本机运行 Harness；OpenAI 模式需在设置中填 API 密钥。");
            return;
        }

        AppendMessage("user", text);
        _lastStatus = "";
        _thinkingShown = false;
        _busy = true;
        SendBtn.IsEnabled = false;
        StopBtn.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();

        // OpenAI 模式：先建 assistant 气泡，用 progress 追加；Harness 模式：由观察者按需创建
        ChatItem? openAiBubble = null;
        if (!IsHarness)
        {
            openAiBubble = AppendMessage("assistant", "");
        }
        var bubble = openAiBubble;
        var progress = new Progress<string>(s => Dispatcher.Invoke(() =>
        {
            bubble ??= AppendMessage("assistant", "");
            bubble.Append(s);
            ScrollToEnd();
        }));

        _ = Task.Run(async () =>
        {
            try
            {
                if (IsHarness)
                {
                    await HarnessClient.AskAsync(cfg, _currentSessionId, text, this, _cts.Token);
                }
                else
                {
                    _openaiHistory.Add(("user", text));
                    await AiClient.AskAsync(cfg, _openaiHistory, text, progress, _cts.Token);
                    Dispatcher.Invoke(() => _openaiHistory.Add(("assistant", bubble?.Text ?? "")));
                }
            }
            catch (OperationCanceledException)
            {
                if (!IsHarness) Dispatcher.Invoke(() => _openaiHistory.Add(("assistant", bubble?.Text ?? "")));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    if (bubble == null || bubble.Text.Length == 0)
                    {
                        var errItem = AppendMessage("assistant", $"⚠️ {ex.Message}");
                        _ = errItem;
                    }
                    else
                    {
                        bubble.Append($"\n\n⚠️ {ex.Message}");
                    }
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
        // 清理空 assistant 气泡（如回合只产生了工具调用）
        if (_messages.Count > 0 && _messages[^1].Kind == "assistant" && _messages[^1].Text.Length == 0)
        {
            _messages.RemoveAt(_messages.Count - 1);
        }
        InputBox.Focus();
        ScrollToEnd();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        var cfg = ((App)Application.Current).CurrentConfig;
        if (IsHarness)
        {
            _ = Task.Run(async () => { try { await HarnessClient.CancelAsync(cfg, CancellationToken.None); } catch { } });
        }
    }

    // ==================== Harness 观察者（后台线程调用，需切 UI） ====================

    void IHarnessObserver.OnTextDelta(string text) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            DebugLog.Write($"OnTextDelta: +{text.Length} chars, msgCount={_messages.Count}");
            // 若末尾不是 assistant 气泡则新建（文本应显示在工具状态之后）
            if (_messages.Count == 0 || _messages[^1].Kind != "assistant")
            {
                _messages.Add(new ChatItem { Kind = "assistant", Text = "" });
            }
            _messages[^1].Append(text);
            ScrollToEnd();
        }));

    void IHarnessObserver.OnStatus(string status) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (status == _lastStatus) return; // 连续同状态去重
            _lastStatus = status;
            if (status == "🧠 思考中…" && _thinkingShown) return; // 每轮只显示一次
            if (status == "🧠 思考中…") _thinkingShown = true;
            AppendMessage("status", status);
            ScrollToEnd();
        }));

    void IHarnessObserver.OnApproval(HarnessApproval approval) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _pendingApproval = approval;
            AppendMessage("status", $"⚠️ 需要授权：使用工具【{approval.ToolName}】{(string.IsNullOrEmpty(approval.Reason) ? "" : $"（{approval.Reason}）")}");
            ActionText.Text = $"是否允许 Harness 使用工具【{approval.ToolName}】？";
            ApprovalBtns.Visibility = Visibility.Visible;
            QuestionBox.Visibility = Visibility.Collapsed;
            ActionBar.Visibility = Visibility.Visible;
        }));

    void IHarnessObserver.OnQuestion(HarnessQuestion question) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _pendingQuestion = question;
            AppendMessage("status", $"❓ {question.Question}");
            ActionText.Text = question.Options is { Count: > 0 }
                ? $"{question.Question}\n可选：{string.Join(" / ", question.Options)}"
                : question.Question;
            ApprovalBtns.Visibility = Visibility.Collapsed;
            QuestionBox.Visibility = Visibility.Visible;
            ActionBar.Visibility = Visibility.Visible;
            AnswerInput.Focus();
        }));

    // ==================== 授权 / 提问回复 ====================

    private async void OnAllow(object sender, RoutedEventArgs e) => await RespondApprovalAsync("allowed-once");

    private async void OnReject(object sender, RoutedEventArgs e) => await RespondApprovalAsync("rejected");

    private async Task RespondApprovalAsync(string outcome)
    {
        if (_pendingApproval == null) return;
        var approval = _pendingApproval;
        _pendingApproval = null;
        ActionBar.Visibility = Visibility.Collapsed;
        var cfg = ((App)Application.Current).CurrentConfig;
        try
        {
            await HarnessClient.RespondAsync(cfg.HarnessBaseUrl, approval.RpcId,
                new { approvalId = approval.ApprovalId, outcome }, CancellationToken.None);
            AppendMessage("status", outcome == "allowed-once" ? "✅ 已允许" : "🚫 已拒绝");
        }
        catch (Exception ex)
        {
            AppendMessage("status", $"⚠️ 授权回复失败：{ex.Message}");
        }
    }

    private async void OnAnswerSend(object sender, RoutedEventArgs e)
    {
        if (_pendingQuestion == null) return;
        var question = _pendingQuestion;
        var answer = AnswerInput.Text.Trim();
        if (answer.Length == 0) return;
        _pendingQuestion = null;
        AnswerInput.Clear();
        ActionBar.Visibility = Visibility.Collapsed;
        var cfg = ((App)Application.Current).CurrentConfig;
        try
        {
            await HarnessClient.RespondAsync(cfg.HarnessBaseUrl, question.RpcId,
                new { answer = new { answers = new[] { new { id = question.QuestionId, answer } } } },
                CancellationToken.None);
            AppendMessage("user", answer);
        }
        catch (Exception ex)
        {
            AppendMessage("status", $"⚠️ 回答提交失败：{ex.Message}");
        }
    }

    // ==================== 消息辅助 ====================

    private ChatItem AppendMessage(string kind, string text)
    {
        var item = new ChatItem { Kind = kind, Text = text };
        _messages.Add(item);
        ScrollToEnd();
        return item;
    }

    private void ScrollToEnd() => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => MsgScroll.ScrollToEnd()));

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
