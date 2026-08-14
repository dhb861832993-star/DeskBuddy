using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using QuickMenu.Models;

namespace QuickMenu.Services;

/// <summary>
/// 本机 DeepSeek Harness 客户端：通过其本地 RPC API（HTTP POST /api/*）
/// 发送消息，并通过 WebSocket /api/events.mux 实时接收回复分块。
/// 这样 QuickMenu 的 AI 按钮直接和本机 Harness（agent）对话。
/// </summary>
public static class HarnessClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>最近一次对话使用的会话 ID（供取消操作使用）。</summary>
    public static string? LastSessionId { get; private set; }

    /// <summary>调用 Harness RPC，返回 result.value。</summary>
    private static async Task<JsonElement> RpcAsync(string baseUrl, string method, object payload, CancellationToken ct)
    {
        var envelope = new { type = "client-request", rpcId = Guid.NewGuid().ToString("N"), method, payload };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/{method}")
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json")
        };
        using var resp = await Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("result", out var result))
        {
            if (result.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                return result.TryGetProperty("value", out var v) ? v.Clone() : default;
            }
            var msg = result.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m)
                ? m.GetString()
                : "未知错误";
            throw new InvalidOperationException($"Harness 错误：{msg}");
        }
        throw new InvalidOperationException($"Harness 响应异常：{json[..Math.Min(200, json.Length)]}");
    }

    /// <summary>解析要使用的会话 ID：配置指定 / "new" 新建 / 留空用最近更新的会话。</summary>
    public static async Task<string> ResolveSessionIdAsync(AppConfig cfg, CancellationToken ct)
    {
        var id = cfg.HarnessSessionId?.Trim() ?? "";
        if (id == "new")
        {
            var newSession = await RpcAsync(cfg.HarnessBaseUrl, "session.create", new { }, ct);
            return newSession.GetProperty("sessionId").GetString() ?? throw new InvalidOperationException("创建会话失败");
        }
        if (id.Length > 0) return id;

        var value = await RpcAsync(cfg.HarnessBaseUrl, "session.list", new { }, ct);
        if (value.TryGetProperty("items", out var items))
        {
            string? best = null;
            long bestT = -1;
            foreach (var it in items.EnumerateArray())
            {
                if (!it.TryGetProperty("sessionId", out var sidEl)) continue;
                long t = it.TryGetProperty("updatedAt", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt64() : 0;
                if (t > bestT)
                {
                    bestT = t;
                    best = sidEl.GetString();
                }
            }
            if (best != null) return best;
        }
        var created = await RpcAsync(cfg.HarnessBaseUrl, "session.create", new { }, ct);
        return created.GetProperty("sessionId").GetString() ?? throw new InvalidOperationException("创建会话失败");
    }

    /// <summary>
    /// 向 Harness 会话发送消息并实时接收回复（通过 progress 逐段上报）。
    /// 回复文本 = assistant/chunk 中 chunk.type == "text-delta" 的内容拼接。
    /// </summary>
    public static async Task AskAsync(AppConfig cfg, string userText, IProgress<string>? progress, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(cfg.HarnessBaseUrl) ? "http://127.0.0.1:3080" : cfg.HarnessBaseUrl.TrimEnd('/');
        var sessionId = await ResolveSessionIdAsync(cfg, ct);
        LastSessionId = sessionId;

        var wsUrl = (baseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss://" : "ws://") +
                    baseUrl[(baseUrl.IndexOf("://") + 3)..] + "/api/events.mux";

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(wsUrl), ct);

        // 先连接事件流，再发消息，避免漏掉早期分块
        await RpcAsync(baseUrl, "session.prompt", new
        {
            sessionId,
            mode = "queue",
            content = new[] { new { type = "text", text = userText } }
        }, ct);

        var buf = new byte[131072];
        while (ws.State == WebSocketState.Open)
        {
            var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
            if (res.MessageType == WebSocketMessageType.Close) break;
            if (res.MessageType != WebSocketMessageType.Text) continue;

            var text = Encoding.UTF8.GetString(buf, 0, res.Count);
            if (!TryExtractEvent(text, out var evType, out var chunkType, out var delta))
            {
                continue;
            }

            if (evType == "assistant/chunk" && chunkType == "text-delta" && delta != null)
            {
                progress?.Report(delta);
            }
            else if (evType is "turn/end" or "assistant/message")
            {
                break; // 回合结束
            }
        }

        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
        catch { }
    }

    /// <summary>停止当前回合（Harness 侧取消）。</summary>
    public static async Task CancelAsync(AppConfig cfg, CancellationToken ct)
    {
        if (LastSessionId == null) return;
        try
        {
            var baseUrl = string.IsNullOrWhiteSpace(cfg.HarnessBaseUrl) ? "http://127.0.0.1:3080" : cfg.HarnessBaseUrl.TrimEnd('/');
            await RpcAsync(baseUrl, "session.cancel", new { sessionId = LastSessionId }, ct);
        }
        catch { }
    }

    /// <summary>从 WS 文本帧中解析 session/event 事件的关键字段。</summary>
    private static bool TryExtractEvent(string frame, out string? evType, out string? chunkType, out string? delta)
    {
        evType = null; chunkType = null; delta = null;
        try
        {
            using var doc = JsonDocument.Parse(frame);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() != "server-request") return false;
            if (root.GetProperty("method").GetString() != "session/event") return false;

            var ev = root.GetProperty("payload").GetProperty("event");
            evType = ev.GetProperty("type").GetString();
            if (evType == "assistant/chunk")
            {
                var chunk = ev.GetProperty("data").GetProperty("chunk");
                chunkType = chunk.GetProperty("type").GetString();
                if (chunk.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    delta = t.GetString();
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
