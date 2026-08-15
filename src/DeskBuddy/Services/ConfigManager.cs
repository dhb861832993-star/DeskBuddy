using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBuddy.Models;

namespace DeskBuddy.Services;

public static class ConfigManager
{
    /// <summary>配置文件路径（位于程序同目录）。</summary>
    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "DeskBuddy.config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppConfig Load()
    {
        try
        {
            // 旧版 QuickMenu 配置迁移：新文件名不存在但旧文件存在时自动复制一次
            var legacy = Path.Combine(AppContext.BaseDirectory, "QuickMenu.config.json");
            if (!File.Exists(ConfigPath) && File.Exists(legacy))
            {
                try { File.Copy(legacy, ConfigPath); } catch { }
            }

            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
                return Normalize(cfg);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"配置文件读取失败：{ex.Message}\n\n将重建默认配置。", "DeskBuddy");
        }
        var def = DefaultConfig();
        Save(def);
        return def;
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"配置文件保存失败：{ex.Message}", "DeskBuddy");
        }
    }

    private static AppConfig Normalize(AppConfig c)
    {
        if (string.IsNullOrWhiteSpace(c.Hotkey)) c.Hotkey = "Ctrl";
        if (c.DoubleTapIntervalMs <= 100) c.DoubleTapIntervalMs = 380;
        if (c.WindowWidth < 480) c.WindowWidth = 680;
        if (c.MaxWindowHeight < 300) c.MaxWindowHeight = 560;
        if (string.IsNullOrWhiteSpace(c.Theme)) c.Theme = "auto";
        c.Items ??= new List<DeskBuddyItem>();
        return c;
    }

    /// <summary>首次运行生成的默认配置。</summary>
    public static AppConfig DefaultConfig() => new()
    {
        Hotkey = "Ctrl",
        DoubleTapIntervalMs = 380,
        Theme = "auto",
        WindowWidth = 680,
        MaxWindowHeight = 560,
        Items = new List<DeskBuddyItem>
        {
            new() { Name = "记事本",      Type = "app",     Path = "notepad",  Keywords = "文本 编辑 txt" },
            new() { Name = "计算器",      Type = "app",     Path = "calc",     Keywords = "计算" },
            new() { Name = "画图",        Type = "app",     Path = "mspaint",  Keywords = "图片 绘图" },
            new() { Name = "命令提示符",   Type = "app",     Path = "cmd",      Keywords = "终端 命令行 cmd" },
            new() { Name = "文件资源管理器", Type = "app",   Path = "explorer", Keywords = "我的电脑 文件夹" },
            new() { Name = "系统设置",     Type = "command", Path = "control",  Args = "", Keywords = "设置 控制面板" },
            new() { Name = "GitHub",      Type = "url",     Path = "https://github.com", Keywords = "代码 仓库" },
            new() { Name = "百度",        Type = "url",     Path = "https://www.baidu.com", Keywords = "搜索" },
        }
    };
}
