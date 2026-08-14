namespace QuickMenu.Models;

/// <summary>应用配置（对应 QuickMenu.config.json）。</summary>
public class AppConfig
{
    /// <summary>双击呼出按键：Ctrl | Alt | Shift | CapsLock | Win</summary>
    public string Hotkey { get; set; } = "Ctrl";

    /// <summary>两次按键视为“双击”的最大间隔（毫秒）。</summary>
    public int DoubleTapIntervalMs { get; set; } = 380;

    /// <summary>主题：auto | light | dark</summary>
    public string Theme { get; set; } = "auto";

    /// <summary>菜单宽度。</summary>
    public double WindowWidth { get; set; } = 680;

    /// <summary>菜单最大高度。</summary>
    public double MaxWindowHeight { get; set; } = 560;

    /// <summary>菜单条目。</summary>
    public List<QuickMenuItem> Items { get; set; } = new();

    // ===== AI 对话（OpenAI 兼容接口，默认 DeepSeek 官方） =====

    /// <summary>API 基础地址，如 https://api.deepseek.com/v1</summary>
    public string AiBaseUrl { get; set; } = "https://api.deepseek.com/v1";

    /// <summary>API 密钥（DeepSeek 开放平台申请）。</summary>
    public string AiApiKey { get; set; } = "";

    /// <summary>模型名，如 deepseek-chat（也兼容任意 OpenAI 兼容端点）。</summary>
    public string AiModel { get; set; } = "deepseek-chat";

    /// <summary>系统提示词。</summary>
    public string AiSystemPrompt { get; set; } = "你是一个简洁的 AI 助手，用中文回答，回答尽量精炼。";

    /// <summary>AI 是否已可用（配了密钥）。</summary>
    public bool AiEnabled => !string.IsNullOrWhiteSpace(AiApiKey);
}
