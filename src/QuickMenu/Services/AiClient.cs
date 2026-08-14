using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QuickMenu.Models;

namespace QuickMenu.Services;

/// <summary>OpenAI 兼容的流式对话客户端（SSE），默认对接 DeepSeek 官方 API。</summary>
public static class AiClient
{
    /// <summary>
    /// 发送一条用户消息，流式产出回复内容（通过 progress 逐段上报，需自行切回 UI 线程）。
    /// 抛出异常表示失败（网络 / 鉴权 / 响应格式）。
    /// </summary>
    public static async Task AskAsync(
        AppConfig cfg,
        IReadOnlyList<(string Role, string Content)> history,
        string userText,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.AiApiKey))
        {
            throw new InvalidOperationException("未配置 AI API 密钥，请到 设置 → AI 对话 中填写。");
        }

        var baseUrl = string.IsNullOrWhiteSpace(cfg.AiBaseUrl) ? "https://api.deepseek.com/v1" : cfg.AiBaseUrl.TrimEnd('/');
        var url = baseUrl + "/chat/completions";

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(cfg.AiSystemPrompt))
        {
            messages.Add(new { role = "system", content = cfg.AiSystemPrompt });
        }
        foreach (var (role, content) in history)
        {
            messages.Add(new { role, content });
        }
        messages.Add(new { role = "user", content = userText });

        var body = JsonSerializer.Serialize(new
        {
            model = string.IsNullOrWhiteSpace(cfg.AiModel) ? "deepseek-chat" : cfg.AiModel,
            messages,
            stream = true,
            temperature = 0.7
        });

        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.AiApiKey);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"请求失败（{(int)resp.StatusCode}）：{err}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;

            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                continue;
            }
            var delta = choices[0].GetProperty("delta");
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                var s = content.GetString();
                if (!string.IsNullOrEmpty(s)) progress?.Report(s);
            }
        }
    }
}
