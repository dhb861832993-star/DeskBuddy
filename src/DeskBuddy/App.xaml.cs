using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using DeskBuddy.Models;
using DeskBuddy.Services;

namespace DeskBuddy;

public partial class App : Application
{
    private const string MutexName = @"Global\DeskBuddy_SingleInstance";
    private const string ShowSignalName = @"Global\DeskBuddy_ShowSignal";

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

    private CancellationTokenSource? _mcpPipeCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---- MCP 无头模式：DeskBuddy.exe --mcp（供 AI 工具通过 MCP 调用）----
        if (e.Args.Contains("--mcp", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunMcpHeadless();
            return;
        }

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
        UpdateMcpPipe(); // 按配置决定是否监听 MCP 管道

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

        // ---- 呼出热键（v2：单键=双击检测器 / 组合键=按下直发） ----
        var menuVkForTap = ParseMenuHotkey(_config.Hotkey);
        _detector = new DoubleTapDetector(menuVkForTap, _config.DoubleTapIntervalMs);   // 组合模式 vk=0 不参与
        _detector.DoubleTapped += ToggleMenu;
        _hook = new KeyboardHook();
        _hook.KeyDown += OnGlobalKeyDown;
        _hook.KeyUp += k => _detector.OnKeyUp(k);

        // 启动时立即解析截图热键（否则 SnipVk/_snipMods 停留在默认值，配置里的组合键不生效）
        ParseSnipHotkey(_config.SnipHotkey);

        // 预创建截图覆盖窗口（含每屏子窗口，隐藏待命）→ F1 激活接近 0ms
        Tools.SnipOverlayWindow.PreCreate();

        // 调试触发器（仅 QM_DEBUG=1 时启用）：通过 deskbuddy_trigger.txt 模拟热键/输入
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
        var path = Path.Combine(AppContext.BaseDirectory, "deskbuddy_trigger.txt");
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
            case "snip": StartSnip(); break;
            default: ToggleMenu(); break;
        }
    }

    /// <summary>应用新的配置（保存 + 重配热键检测 + 更新托盘提示）。</summary>
    public void ApplyConfig(AppConfig cfg)
    {
        _config = cfg;
        ConfigManager.Save(cfg);
        var vk = ParseMenuHotkey(cfg.Hotkey);   // 单键→双击 vk；组合键→0（不参与双击）
        ParseSnipHotkey(cfg.SnipHotkey);
        if (_detector != null)
        {
            var (curVk, curInterval) = _detector.Current;
            if (curVk != vk || curInterval != cfg.DoubleTapIntervalMs)
            {
                _detector.Reconfigure(vk, cfg.DoubleTapIntervalMs);
            }
        }
        _tray?.UpdateHotkeyText(HotkeyDisplay(cfg.Hotkey));
        UpdateMcpPipe(); // MCP 开关变化时启停管道监听
    }

    // ==================== MCP（AI 快捷添加菜单） ====================

    /// <summary>按 McpEnabled 决定是否启动/停止 MCP 命名管道监听。</summary>
    private void UpdateMcpPipe()
    {
        _mcpPipeCts?.Cancel();
        _mcpPipeCts?.Dispose();
        _mcpPipeCts = null;
        if (_config.McpEnabled)
        {
            _mcpPipeCts = new CancellationTokenSource();
            McpPipe.StartServer(HandleMcpRequest, _mcpPipeCts.Token);
            DebugLog.Write("MCP pipe server started");
        }
        else
        {
            DebugLog.Write("MCP pipe server stopped");
        }
    }

    /// <summary>处理 MCP 子进程转发来的请求（管道线程调用；配置操作切到 UI 线程）。</summary>
    private string HandleMcpRequest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var op = root.TryGetProperty("op", out var opEl) ? opEl.GetString() : null;
            switch (op)
            {
                case "ping":
                    return "{\"ok\":true,\"message\":\"pong\"}";
                case "add":
                    if (!root.TryGetProperty("item", out var itemEl)) return McpErr("缺少 item 字段");
                    var item = itemEl.Deserialize<DeskBuddy.Models.DeskBuddyItem>(McpJsonOpts);
                    if (item == null) return McpErr("item 解析失败");
                    return Dispatcher.Invoke(() => _mainWindow?.McpAddItem(item) ?? McpErr("主窗口未就绪"));
                case "remove":
                    var name = root.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                    return Dispatcher.Invoke(() => _mainWindow?.McpRemoveItem(name) ?? McpErr("主窗口未就绪"));
                case "list":
                    return Dispatcher.Invoke(() => _mainWindow?.McpListItems() ?? McpErr("主窗口未就绪"));
                default:
                    return McpErr($"未知操作：{op}");
            }
        }
        catch (Exception ex)
        {
            return McpErr(ex.Message);
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions McpJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static string McpErr(string message) =>
        System.Text.Json.JsonSerializer.Serialize(new { ok = false, message });

    /// <summary>--mcp 无头模式：不建窗口/托盘/热键，只跑 MCP 服务，退出即关闭。</summary>
    private async void RunMcpHeadless()
    {
        try
        {
            await McpService.RunAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine($"DeskBuddy MCP 错误：{ex.Message}"); } catch { }
        }
        Shutdown();
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

    /// <summary>全局按键处理：呼出（单键双击/组合单击）+ 截图热键 + 全局 Esc 退出。</summary>
    private void OnGlobalKeyDown(int vkCode)
    {
        _detector.OnKeyDown(vkCode);   // 单键模式（组合模式 vk=0 永不匹配）
        if (vkCode == 0x1B) HandleGlobalEscape();
        // 呼出：组合键模式 → 主键按下且修饰键精确匹配 → 立即呼出（节流 350ms 防自动重复连发）
        if (_menuMods.Length > 0 && vkCode == _menuVk && ModsMatchCurrent(_menuMods))
        {
            var now = Environment.TickCount64;
            if (now - _menuLastTrigger > 350)
            {
                _menuLastTrigger = now;
                ToggleMenu();
            }
        }
        if (vkCode == SnipVk && ModMatch())
        {
            if (!_snipActive || !Tools.SnipOverlayWindow.IsReallyActive()) StartSnip();
        }
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    /// <summary>当前修饰键组合是否匹配配置（Win 键不参与匹配，避免误伤）。</summary>
    private static bool ModMatch()
    {
        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        var m = _snipMods;
        bool needCtrl = m.Contains("Ctrl"), needAlt = m.Contains("Alt"), needShift = m.Contains("Shift");
        return ctrl == needCtrl && alt == needAlt && shift == needShift;
    }

    // ==================== 截图 ====================

    private static int SnipVk = 0x70;      // 主键虚拟码
    private static string _snipMods = "";   // 修饰键组合（"Ctrl+Alt" 等，空 = 无）

    /// <summary>配置串 → 虚拟键码（A-Z / 0-9 / Num0-9 / F1-F12 / PrtSc / Space / ` / CapsLock / Win；呼出与截图共用）。</summary>
    private static int VkOf(string key) => key?.Trim() switch
    {
        "PrintScreen" or "PRINTSCREEN" => 0x2C,
        "Print" or "printscreen" => 0x2C,
        "Space" or "SPACE" or "space" => 0x20,
        "CapsLock" or "CAPSLOCK" or "capslock" => 0x14,
        "Win" or "WIN" or "win" => 0x5B,
        "`" => 0xC0,
        _ when key!.Length == 1 && char.IsDigit(key[0]) => 0x30 + (key[0] - '0'),
        _ when key.Length == 1 && key[0] >= 'A' && key[0] <= 'Z' => 0x41 + (key[0] - 'A'),
        _ when key.Length == 1 && key[0] >= 'a' && key[0] <= 'z' => 0x41 + (key[0] - 'a'),
        _ when key.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && key.Length == 4 && key[3] >= '0' && key[3] <= '9' => 0x60 + (key[3] - '0'),
        _ when key.Length >= 2 && (key[0] == 'F' || key[0] == 'f') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24 => 0x70 + (f - 1),
        _ => 0
        ,
    };

    /// <summary>从配置解析截图热键（支持 "Ctrl+Alt+S"、"F1"、"Ctrl+Shift+PrintScreen" 等格式）。</summary>
    public static void ParseSnipHotkey(string hotkey)
    {
        hotkey ??= "F1";
        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mods = parts.Where(p => p is "Ctrl" or "Alt" or "Shift").ToList();
        var key = parts.FirstOrDefault(p => p is not ("Ctrl" or "Alt" or "Shift")) ?? "F1";
        _snipMods = string.Join("+", mods);
        SnipVk = VkOf(key);
        DebugLog.Write($"[SNIP] hotkey parsed: '{hotkey}' -> vk=0x{SnipVk:X2} mods='{_snipMods}'");
    }

    // 旧配置兼容（单键格式）
    public static int VkFromSnipHotkey(string name) { ParseSnipHotkey(name); return SnipVk; }

    private bool _snipActive;

    /// <summary>启动截图覆盖层。钩子线程调用需切 UI 线程（防跨线程窗口操作崩溃）。</summary>
    public void StartSnip()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(StartSnip); return; }
        ActivatedSnip();
    }

    private void ActivatedSnip()
    {
        if (_snipActive && Tools.SnipOverlayWindow.IsReallyActive()) return;   // 激活中忽略
        _snipActive = true;
        try
        {
            _mainWindow?.HideMenu();
            var win = Tools.SnipOverlayWindow.Activate();   // 新建实例（截屏+建窗+显示）
            win.Closed += (_, _) => _snipActive = false;    // 新实例会真 Close → Closed 正常触发
            win.StopRequested += () => _snipActive = false;
        }
        catch { _snipActive = false; }
    }

    private bool _stopHooked;

    /// <summary>按 Esc 必须退出：截图覆盖层 → 右键菜单 → AI 对话 → 编辑器 → 设置 → 菜单。</summary>
    /// <summary>按 Esc 必须退出（钩子线程进来 → 切 UI 线程防崩）。</summary>
    private void HandleGlobalEscape()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(HandleGlobalEscape); return; }
        DebugLog.Write("global Esc pressed");
        // 最高优先：截图覆盖层激活时，Esc 退出截图（覆盖窗口可能没焦点，必须这里兜底）
        if (Tools.SnipOverlayWindow.IsReallyActive())
        {
            var w = Tools.SnipOverlayWindow._pooled;
            w?.StopAndClose();
            DebugLog.Write("[SNIP] stopped by global Esc");
            return;
        }
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

    /// <summary>切换主菜单。⚠调用方可能来自非 UI 线程（KeyboardHook 回调线程）——
    /// 必须切回 Dispatcher：跨线程改 UI Window 是 argc0 崩溃源（Window.Show 的 RootVisual
    /// 挂接对线程有亲和性）。写 DebugLog 前先切。</summary>
    private void ToggleMenu()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ToggleMenu);
            return;
        }
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
            MessageBox.Show($"无法打开配置文件：{ex.Message}", "DeskBuddy");
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

    // ==================== 呼出热键（v2：任意单键双击 / 任意组合键单击） ====================

    private static int _menuVk = 0x12;        // 呼出主键 vk
    private static string _menuMods = "";     // 组合模式修饰键（"Ctrl+Alt"；空 = 单键双击模式）
    private static long _menuLastTrigger;     // 单击模式节流（防按键自动重复连发）

    /// <summary>解析呼出热键：单键（Alt/CapsLock/F7/任意键）→ 双击模式（兼容旧配置&防误触）；
    /// 组合键（Ctrl+Alt+Q 等）→ 按下即呼出。返回 detector 应使用的 vk（组合模式返回 0 = 不参与双击）。</summary>
    private static int ParseMenuHotkey(string? hotkey)
    {
        hotkey = (hotkey ?? "Alt").Trim();
        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var vkFull = hotkey?.ToUpperInvariant() switch   // 旧单修饰键兼容（含 CapsLock/Win）
        {
            "ALT" => 0x12,
            "SHIFT" => 0x10,
            "CAPSLOCK" => 0x14,
            "WIN" => 0x5B,
            _ => -1
        };
        if (parts.Length == 1)
        {
            _menuMods = "";
            if (vkFull > 0) { _menuVk = vkFull; DebugLog.Write($"[MENU] hotkey parsed: '{hotkey}' -> vk=0x{_menuVk:X2} mods='' mode=tap(legacy)"); return _menuVk; }
            _menuVk = VkOf(parts[0]);      // 新式任意单键（F7/Q/Space…）
            DebugLog.Write($"[MENU] hotkey parsed: '{hotkey}' -> vk=0x{_menuVk:X2} mods='' mode=tap");
            return _menuVk;
        }
        // 组合键：修饰集合 + 主键
        var mods = parts.Where(p => p is "Ctrl" or "Alt" or "Shift" or "Win").Select(p => p == "Win" ? "Win" : p).ToList();
        var main = parts.FirstOrDefault(p => p is not ("Ctrl" or "Alt" or "Shift" or "Win"));
        if (main == null)
        {
            // 纯多修饰（Ctrl+Alt）：用最后一个做主键单键双击（少见，降级处理）
            _menuMods = "";
            _menuVk = VkOf(parts[^1]);
            return _menuVk;
        }
        _menuMods = string.Join("+", mods);
        _menuVk = VkOf(main);
        DebugLog.Write($"[MENU] hotkey parsed: '{hotkey}' -> vk=0x{_menuVk:X2} mods='{_menuMods}' mode={(mods.Count > 0 && parts.Length > 1 ? "combo" : "tap")}");
        return 0;   // 组合模式：detector 不参与
    }

    /// <summary>当前按下状态是否【精确等于】给定修饰键组合（钩子线程安全：GetAsyncKeyState）。</summary>
    private static bool ModsMatchCurrent(string mods)
    {
        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        bool win = (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
        return ctrl == mods.Contains("Ctrl") && alt == mods.Contains("Alt")
            && shift == mods.Contains("Shift") && win == mods.Contains("Win");
    }

    private static string HotkeyDisplay(string? hotkey)
    {
        hotkey = (hotkey ?? "Alt").Trim();
        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 1 ? $"双击 {parts[0]} 呼出" : $"按 {hotkey} 呼出";
    }
}
