using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using QuickMenu.Models;
using QuickMenu.Services;

namespace QuickMenu;

public partial class App : Application
{
    private const string MutexName = @"Global\QuickMenu_SingleInstance";
    private const string ShowSignalName = @"Global\QuickMenu_ShowSignal";

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private Thread? _signalThread;
    private KeyboardHook? _hook;
    private DoubleTapDetector? _detector;
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;
    private ChatWindow? _chatWindow;
    private AppConfig _config = new();
    private MainWindow? _mainWindow;

    /// <summary>当前配置（供聊天窗口等读取）。</summary>
    public AppConfig CurrentConfig => _config;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---- 单实例：已运行时，通知旧实例弹出菜单并退出 ----
        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            try { EventWaitHandle.OpenExisting(ShowSignalName).Set(); } catch { }
            Shutdown();
            return;
        }

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        _signalThread = new Thread(() =>
        {
            while (true)
            {
                try { _showSignal.WaitOne(); }
                catch { break; }
                Dispatcher.Invoke(() => _mainWindow?.ShowMenu());
            }
        })
        { IsBackground = true, Name = "ShowSignalListener" };
        _signalThread.Start();

        // ---- 配置 ----
        _config = ConfigManager.Load();
        _mainWindow = new MainWindow();
        _mainWindow.ConfigChanged += OnConfigChanged;

        // ---- 托盘 ----
        _tray = new TrayService();
        _tray.ShowMenuRequested += ToggleMenu;
        _tray.SettingsRequested += OpenSettings;
        _tray.EditConfigRequested += EditConfig;
        _tray.ReloadConfigRequested += () =>
        {
            _config = ConfigManager.Load();
            _mainWindow.ShowMenu();
        };
        _tray.AutoStartChanged += enabled => AutoStart.Set(enabled);
        _tray.ExitRequested += ExitApp;
        _tray.UpdateHotkeyText(HotkeyDisplay(_config.Hotkey));

        // ---- 全局双击热键 ----
        _detector = new DoubleTapDetector(VkFromHotkey(_config.Hotkey), _config.DoubleTapIntervalMs);
        _detector.DoubleTapped += ToggleMenu;
        _hook = new KeyboardHook();
        _hook.KeyDown += OnGlobalKeyDown;
        _hook.KeyUp += k => _detector.OnKeyUp(k);

        // 调试触发器（仅 QM_DEBUG=1 时启用）：通过 qm_trigger.txt 模拟热键/输入
        if (DebugLog.Enabled)
        {
            var dbgTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            dbgTimer.Tick += (_, _) => DebugTriggerCheck();
            dbgTimer.Start();
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    private void DebugTriggerCheck()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "qm_trigger.txt");
        if (!File.Exists(path)) return;
        string cmd;
        try { cmd = File.ReadAllText(path).Trim(); File.Delete(path); }
        catch { return; }
        DebugLog.Write("debug trigger: " + cmd);

        if (cmd == "exit") { ExitApp(); return; }
        if (cmd.StartsWith("type:", StringComparison.Ordinal))
        {
            _mainWindow?.SetSearchText(cmd["type:".Length..]);
            return;
        }
        if (cmd.StartsWith("ctx:", StringComparison.Ordinal))
        {
            _mainWindow?.DebugCtxAction(cmd["ctx:".Length..].Trim());
            return;
        }
        if (cmd.StartsWith("ai:", StringComparison.Ordinal))
        {
            OpenChat(cmd["ai:".Length..].Trim());
            return;
        }
        if (cmd.StartsWith("drop:", StringComparison.Ordinal))
        {
            var paths = cmd["drop:".Length..].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var items = ItemDropHandler.FromFiles(paths);
            if (items.Count > 0)
            {
                if (_settingsWindow is { IsVisible: true }) _settingsWindow.AddDraftItems(items);
                else _mainWindow?.AddItemsAndSave(items);
            }
            return;
        }
        switch (cmd)
        {
            case "show": _mainWindow?.ShowMenu(); break;
            case "hide": _mainWindow?.HideMenu(); break;
            case "enter": _mainWindow?.LaunchSelected(); break;
            case "settings": OpenSettings(); break;
            default: ToggleMenu(); break;
        }
    }

    /// <summary>应用新的配置（保存 + 重配热键检测 + 更新托盘提示）。</summary>
    public void ApplyConfig(AppConfig cfg)
    {
        _config = cfg;
        ConfigManager.Save(cfg);
        var vk = VkFromHotkey(cfg.Hotkey);
        if (_detector != null)
        {
            var (curVk, curInterval) = _detector.Current;
            if (curVk != vk || curInterval != cfg.DoubleTapIntervalMs)
            {
                _detector.Reconfigure(vk, cfg.DoubleTapIntervalMs);
            }
        }
        _tray?.UpdateHotkeyText(HotkeyDisplay(cfg.Hotkey));
    }

    /// <summary>打开设置窗口（已打开则激活）。</summary>
    public void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _mainWindow?.HideMenu();
        _settingsWindow = new SettingsWindow(_config);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnConfigChanged(AppConfig cfg) => ApplyConfig(cfg);

    /// <summary>全局按键处理：双击检测 + 全局 Esc 退出（不依赖窗口焦点）。</summary>
    private void OnGlobalKeyDown(int vkCode)
    {
        _detector.OnKeyDown(vkCode);
        if (vkCode == 0x1B) HandleGlobalEscape();
    }

    /// <summary>按 Esc 必须退出：优先关图标右键菜单 → AI 对话 → 编辑器 → 设置 → 菜单。</summary>
    private void HandleGlobalEscape()
    {
        DebugLog.Write("global Esc pressed");
        if (_mainWindow is { } mw && mw.CloseContextMenuIfOpen())
        {
            return; // 先关右键菜单
        }
        if (_chatWindow is { IsVisible: true })
        {
            _chatWindow.Hide();
            return;
        }
        if (ItemEditorWindow.Current is { } editor)
        {
            editor.Close();
            return;
        }
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.HandleGlobalEscape();
            return;
        }
        _mainWindow?.HideMenu();
    }

    /// <summary>打开 AI 对话窗口（initial 非空则直接提问）。</summary>
    public void OpenChat(string? initial = null)
    {
        _chatWindow ??= new ChatWindow();
        _chatWindow.ShowAndAsk(initial);
    }

    private void ToggleMenu()
    {
        DebugLog.Write("ToggleMenu called");
        if (_settingsWindow is { IsVisible: true } || ItemEditorWindow.IsOpen)
        {
            DebugLog.Write("ignored: settings/editor window open");
            return; // 设置或编辑窗口打开时不响应双击，避免干扰
        }
        if (_mainWindow!.IsVisible) _mainWindow.HideMenu();
        else _mainWindow.ShowMenu();
    }

    private void EditConfig()
    {
        ConfigManager.Save(_config);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = ConfigManager.ConfigPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开配置文件：{ex.Message}", "QuickMenu");
        }
    }

    private void ExitApp()
    {
        _tray?.Dispose();
        _hook?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        _tray?.Dispose();
        try { _showSignal?.Set(); } catch { }
        _signalThread = null;
        _showSignal?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static int VkFromHotkey(string hotkey) => hotkey?.ToUpperInvariant() switch
    {
        "ALT" => 0x12,
        "SHIFT" => 0x10,
        "CAPSLOCK" => 0x14,
        "WIN" => 0x5B,
        _ => 0x11 // Ctrl
    };

    private static string HotkeyDisplay(string hotkey) => hotkey?.ToUpperInvariant() switch
    {
        "ALT" => "Alt",
        "SHIFT" => "Shift",
        "CAPSLOCK" => "CapsLock",
        "WIN" => "Win",
        _ => "Ctrl"
    };
}
