using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using QuickMenu.Models;

namespace QuickMenu.Services;

/// <summary>Harness 会话摘要（用于会话列表）。</summary>
public sealed class HarnessSession
{
    public required string SessionId { get; init; }
    public string Title { get; init; } = "";
    public long UpdatedAt { get; init; }
    public bool Running { get; init; }

    public string Display => string.IsNullOrWhiteSpace(Title) ? "新会话" : Title;
}

/// <summary>一次工具授权请求。</summary>
public sealed class HarnessApproval
{
    public required string RpcId { get; init; }
    public required string ApprovalId { get; init; }
    public required string ToolName { get; init; }
    public string? Reason { get; init; }
}

/// <summary>一次提问请求。</summary>
public sealed class HarnessQuestion
{
    public required string RpcId { get; init; }
    public required string QuestionId { get; init; }
    public required string Question { get; init; }
    public string? Header { get; init; }
    public List<string>? Options { get; init; }
}

/// <summary>对话过程的观察者（由 UI 实现）。</summary>
public interface IHarnessObserver
{
    void OnTextDelta(string text);
    void OnStatus(string status);
    void OnApproval(HarnessApproval approval);
    void OnQuestion(HarnessQuestion question);
}

/// <summary>
/// 本机 DeepSeek Harness 客户端：HTTP RPC（POST /api/*）+ WebSocket 事件流。
/// 支持实时流式回复、工具/步骤状态、授权与提问处理、会话管理与历史加载。
/// </summary>
public static class HarnessClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>最近一次对话使用的会话 ID。</summary>
    public static string? LastSessionId { get; private set; }

    // ==================== 底层 RPC ====================

    /// <summary>调用 Harness RPC（client-request 信封），返回 result.value。</summary>
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
        if (doc.RootElement.TryGetProperty("result", out var result) &&
            result.TryGetProperty("ok", out var ok) && ok.GetBoolean())
        {
            return result.TryGetProperty("value", out var v) ? v.Clone() : default;
        }
        var msg = doc.RootElement.TryGetProperty("result", out var r2) &&
                  r2.TryGetProperty("error", out var err) &&
                  err.TryGetProperty("message", out var m)
            ? m.GetString()
            : $"响应异常：{json[..Math.Min(200, json.Length)]}";
        throw new InvalidOperationException($"Harness 错误：{msg}");
    }

    /// <summary>向 /api/respond 发送 client-response（授权决定 / 问题答案）。</summary>
    public static async Task RespondAsync(string baseUrl, string rpcId, object value, CancellationToken ct)
    {
        var envelope = new { type = "client-response", rpcId, result = new { ok = true, value } };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/respond")
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json")
        };
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    // ==================== 会话管理 ====================

    public static async Task<List<HarnessSession>> ListSessionsAsync(AppConfig cfg, CancellationToken ct)
    {
        var baseUrl = BaseUrl(cfg);
        var value = await RpcAsync(baseUrl, "session.list", new { }, ct);
        var result = new List<HarnessSession>();
        if (!value.TryGetProperty("items", out var items)) return result;
        foreach (var it in items.EnumerateArray())
        {
            if (!it.TryGetProperty("sessionId", out var sidEl)) continue;
            result.Add(new HarnessSession
            {
                SessionId = sidEl.GetString() ?? "",
                Title = ReadTitle(it),
                UpdatedAt = it.TryGetProperty("updatedAt", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt64() : 0,
                Running = it.TryGetProperty("running", out var run) && run.GetBoolean()
            });
        }
        return result.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    /// <summary>会话主题：优先 projections.values.title（Harness 自动总结），其次顶层 title。</summary>
    private static string ReadTitle(JsonElement it)
    {
        if (it.TryGetProperty("projections", out var proj) &&
            proj.TryGetProperty("values", out var vals) &&
            vals.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
        {
            var s = t.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        if (it.TryGetProperty("title", out var top) && top.ValueKind == JsonValueKind.String)
        {
            var s2 = top.GetString();
            if (!string.IsNullOrWhiteSpace(s2)) return s2;
        }
        return "";
    }

    public static async Task<string> CreateSessionAsync(AppConfig cfg, CancellationToken ct)
    {
        var baseUrl = BaseUrl(cfg);
        var created = await RpcAsync(baseUrl, "session.create", new { }, ct);
        return created.GetProperty("sessionId").GetString() ?? throw new InvalidOperationException("创建会话失败");
    }

    /// <summary>按配置解析会话：指定 ID / "new" 新建 / 留空用最近更新的会话。</summary>
    public static async Task<string> ResolveSessionIdAsync(AppConfig cfg, CancellationToken ct)
    {
        var id = cfg.HarnessSessionId?.Trim() ?? "";
        if (id == "new")
        {
            return await CreateSessionAsync(cfg, ct);
        }
        if (id.Length > 0) return id;

        var sessions = await ListSessionsAsync(cfg, ct);
        if (sessions.Count > 0) return sessions[0].SessionId;
        return await CreateSessionAsync(cfg, ct);
    }

    // ==================== 历史 ====================

    /// <summary>读取会话历史，返回可展示的消息行（user/assistant 文本 + 工具摘要）。</summary>
    public static async Task<List<(string Role, string Text)>> GetHistoryAsync(AppConfig cfg, string sessionId, CancellationToken ct)
    {
        var baseUrl = BaseUrl(cfg);
        var value = await RpcAsync(baseUrl, "session.history", new { sessionId }, ct);
        var result = new List<(string, string)>();
        if (!value.TryGetProperty("events", out var events)) return result;

        foreach (var entry in events.EnumerateArray())
        {
            if (!entry.TryGetProperty("event", out var ev)) continue;
            var type = ev.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "user/message")
            {
                var text = ExtractText(ev, "data");
                if (string.IsNullOrWhiteSpace(text)) continue;
                // 跳过系统注入的上下文消息（runtime context 等）
                if (text.StartsWith("Current runtime context", StringComparison.Ordinal) ||
                    text.StartsWith("当前运行时上下文", StringComparison.Ordinal) ||
                    text.Contains("Current DSH file policy"))
                {
                    continue;
                }
                result.Add(("user", text));
            }
            else if (type == "assistant/message")
            {
                var text = ExtractText(ev, "data");
                if (!string.IsNullOrWhiteSpace(text)) result.Add(("assistant", text));
            }
            else if (type == "tool/call")
            {
                var name = ev.TryGetProperty("data", out var d) && d.TryGetProperty("name", out var n) ? n.GetString() : "";
                if (!string.IsNullOrEmpty(name)) result.Add(("status", $"🔧 使用工具：{name}"));
            }
        }
        return result;
    }

    private static string ExtractText(JsonElement ev, string dataProp)
    {
        if (!ev.TryGetProperty(dataProp, out var data)) return "";
        // assistant/message 的文本在 data.message.content，user/message 在 data.content
        var content = data.TryGetProperty("content", out var c1)
            ? c1
            : data.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c2)
                ? c2
                : default;
        if (content.ValueKind != JsonValueKind.Array) return "";
        foreach (var part in content.EnumerateArray())
        {
            if (part.TryGetProperty("type", out var pt) && pt.GetString() == "text" &&
                part.TryGetProperty("text", out var txt))
            {
                return txt.GetString() ?? "";
            }
        }
        return "";
    }

    // ==================== 对话 ====================

    public static async Task AskAsync(AppConfig cfg, string sessionId, string userText, IHarnessObserver observer, CancellationToken ct)
    {
        var baseUrl = BaseUrl(cfg);
        LastSessionId = sessionId;

        var wsUrl = (baseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss://" : "ws://") +
                    baseUrl[(baseUrl.IndexOf("://") + 3)..] + "/api/events.mux";

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(wsUrl), ct);

        // 先连事件流再发消息，避免漏掉早期分块
        await RpcAsync(baseUrl, "session.prompt", new
        {
            sessionId,
            mode = "queue",
            content = new[] { new { type = "text", text = userText } }
        }, ct);

        var buf = new byte[262144];
        while (ws.State == WebSocketState.Open)
        {
            var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
            if (res.MessageType == WebSocketMessageType.Close) break;
            if (res.MessageType != WebSocketMessageType.Text) continue;

            var text = Encoding.UTF8.GetString(buf, 0, res.Count);
            ProcessFrame(text, observer, ct);
        }

        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
        catch { }
    }

    /// <summary>处理一帧 WS 文本（server-request 信封）。</summary>
    private static void ProcessFrame(string frame, IHarnessObserver observer, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(frame);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() != "server-request") return;
            var rpcId = root.GetProperty("rpcId").GetString() ?? "";
            var method = root.GetProperty("method").GetString() ?? "";

            if (method == "session/event")
            {
                var ev = root.GetProperty("payload").GetProperty("event");
                HandleEvent(ev, rpcId, observer);
            }
            else
            {
                // 顶层帧类型（approval/requested、question/requested 等）
                if (!root.TryGetProperty("payload", out var payload)) return;
                var pt = payload.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (pt == "approval/requested") HandleApproval(payload, rpcId, observer);
                else if (pt == "question/requested") HandleQuestion(payload, rpcId, observer);
            }
        }
        catch
        {
            // 忽略无法解析的帧
        }
    }

    private static void HandleEvent(JsonElement ev, string rpcId, IHarnessObserver observer)
    {
        if (!ev.TryGetProperty("type", out var t)) return;
        var type = t.GetString();

        switch (type)
        {
            case "assistant/chunk":
                if (!ev.TryGetProperty("data", out var d) || !d.TryGetProperty("chunk", out var chunk)) return;
                var chunkType = chunk.TryGetProperty("type", out var ct) ? ct.GetString() : null;
                if (chunkType == "text-delta" && chunk.TryGetProperty("text", out var tx) &&
                    tx.ValueKind == JsonValueKind.String)
                {
                    DebugLog.Write($"text-delta: {tx.GetString()?.Length} chars");
                    observer.OnTextDelta(tx.GetString() ?? "");
                }
                else if (chunkType == "reasoning-delta")
                {
                    observer.OnStatus("🧠 思考中…");
                }
                break;

            case "tool/call":
                var name = ev.TryGetProperty("data", out var d2) && d2.TryGetProperty("name", out var n) ? n.GetString() : "";
                observer.OnStatus(string.IsNullOrEmpty(name) ? "🔧 正在调用工具…" : $"🔧 正在使用工具：{name}");
                break;

            case "step/start":
                observer.OnStatus("⏳ 开始处理…");
                break;

            case "approval/requested":
                HandleApproval(ev.TryGetProperty("data", out var ad) ? ad : ev, rpcId, observer);
                break;

            case "question/requested":
                HandleQuestion(ev.TryGetProperty("data", out var qd) ? qd : ev, rpcId, observer);
                break;

            case "turn/end":
                observer.OnStatus("✅ 完成");
                break;
        }
    }

    private static void HandleApproval(JsonElement data, string rpcId, IHarnessObserver observer)
    {
        if (!data.TryGetProperty("approvalId", out var aid)) return;
        observer.OnApproval(new HarnessApproval
        {
            RpcId = rpcId,
            ApprovalId = aid.GetString() ?? "",
            ToolName = data.TryGetProperty("toolName", out var tn) ? tn.GetString() ?? "" : "",
            Reason = data.TryGetProperty("reason", out var r) ? r.GetString() : null
        });
    }

    private static void HandleQuestion(JsonElement data, string rpcId, IHarnessObserver observer)
    {
        if (!data.TryGetProperty("questions", out var qs) || qs.GetArrayLength() == 0) return;
        var q = qs[0];
        observer.OnQuestion(new HarnessQuestion
        {
            RpcId = rpcId,
            QuestionId = q.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            Question = q.TryGetProperty("question", out var qq) ? qq.GetString() ?? "（问题）" : "（问题）",
            Header = q.TryGetProperty("header", out var h) ? h.GetString() : null,
            Options = q.TryGetProperty("options", out var opts)
                ? opts.EnumerateArray().Select(o => o.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "").ToList()
                : null
        });
    }

    /// <summary>停止当前回合。</summary>
    public static async Task CancelAsync(AppConfig cfg, CancellationToken ct)
    {
        if (LastSessionId == null) return;
        try
        {
            await RpcAsync(BaseUrl(cfg), "session.cancel", new { sessionId = LastSessionId }, ct);
        }
        catch { }
    }

    private static string BaseUrl(AppConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.HarnessBaseUrl) ? "http://127.0.0.1:3080" : cfg.HarnessBaseUrl.TrimEnd('/');
}
