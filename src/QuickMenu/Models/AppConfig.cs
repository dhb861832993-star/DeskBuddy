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

    // ===== AI 对话 =====

    /// <summary>接入方式：harness（本机 DeepSeek Harness）| openai（OpenAI 兼容 API）</summary>
    public string AiMode { get; set; } = "harness";

    /// <summary>本机 Harness 地址（默认本地 3080 端口）。</summary>
    public string HarnessBaseUrl { get; set; } = "http://127.0.0.1:3080";

    /// <summary>Harness 会话策略：留空=最近桌面助手会话；"new"=每次都新建；或填具体 sessionId。</summary>
    public string HarnessSessionId { get; set; } = "";

    /// <summary>会话下拉框是否显示全部会话（false=只显示「桌面助手」分组）。</summary>
    public bool HarnessShowAllSessions { get; set; }

    /// <summary>API 基础地址，如 https://api.deepseek.com/v1（openai 模式）。</summary>
    public string AiBaseUrl { get; set; } = "https://api.deepseek.com/v1";

    /// <summary>API 密钥（openai 模式）。</summary>
    public string AiApiKey { get; set; } = "";

    /// <summary>模型名（openai 模式）。</summary>
    public string AiModel { get; set; } = "deepseek-chat";

    /// <summary>系统提示词（openai 模式）。</summary>
    public string AiSystemPrompt { get; set; } = "你是一个简洁的 AI 助手，用中文回答，回答尽量精炼。";

    /// <summary>AI 是否已可用（harness 模式始终可用；openai 模式需密钥）。</summary>
    public bool AiEnabled => AiMode == "harness" || !string.IsNullOrWhiteSpace(AiApiKey);
}
