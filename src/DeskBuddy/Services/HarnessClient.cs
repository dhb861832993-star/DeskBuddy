using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DeskBuddy.Models;

namespace DeskBuddy.Services;

/// <summary>Harness 会话摘要（用于会话列表）。</summary>
public sealed class HarnessSession
{
    public required string SessionId { get; init; }
    public string Title { get; init; } = "";
    public string Cwd { get; init; } = "";
    public long UpdatedAt { get; init; }
    public bool Running { get; init; }

    public string Display => string.IsNullOrWhiteSpace(Title) ? "新会话" : Title;
}

/// <summary>一次工具授权请求。</summary>
public sealed class HarnessApproval
{
    public required string RpcId { get; init; }
    public required string SessionId { get; init; }
    public required string ApprovalId { get; init; }
    public required string ToolName { get; init; }
    public string? Reason { get; init; }
}

/// <summary>一次提问请求。</summary>
public sealed class HarnessQuestion
{
    public required string RpcId { get; init; }
    public required string SessionId { get; init; }
    public required string QuestionId { get; init; }
    public required string Question { get; init; }
    public string? Header { get; init; }
    public List<string>? Options { get; init; }
    public bool MultiSelect { get; init; }
}

/// <summary>对话过程的观察者（由 UI 实现）。</summary>
public interface IHarnessObserver
{
    void OnTextDelta(string text);
    void OnStatus(string status);
    void OnApproval(HarnessApproval approval);
    /// <summary>一次提问批次（agent 可一次问多个问题，必须全部回答）。</summary>
    void OnQuestion(IReadOnlyList<HarnessQuestion> questions);
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

    /// <summary>「桌面助手」分组标记：桌面路径。DeskBuddy 创建的会话都在此 cwd 下。</summary>
    public static string GroupCwd =>
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    /// <summary>会话是否属于「桌面助手」分组（cwd 为桌面路径）。</summary>
    public static bool InGroup(HarnessSession s) =>
        string.Equals(s.Cwd.TrimEnd('\\'), GroupCwd.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

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
        => await SendRespondAsync(baseUrl, rpcId, new { ok = true, value }, ct);

    /// <summary>取消一个挂起的提问（对非当前会话的重放事件做清理）。
    /// 服务端 cancelled 错误分支要求 details 字段必须存在（rpcErrorSchema discriminatedUnion）。</summary>
    public static async Task CancelPendingAsync(string baseUrl, string rpcId, CancellationToken ct)
        => await SendRespondAsync(baseUrl, rpcId, new { ok = false, error = new { code = "cancelled", message = "cancelled by DeskBuddy", details = new { } } }, ct);

    /// <summary>拒绝一个挂起的授权（对非当前会话的重放事件做清理）。
    /// 授权不支持 ok:false 取消，必须回 outcome=rejected 的有效答案。</summary>
    public static async Task RejectApprovalAsync(string baseUrl, string rpcId, string sessionId, string approvalId, CancellationToken ct)
        => await SendRespondAsync(baseUrl, rpcId, new { ok = true, value = new { sessionId, approvalId, outcome = "rejected" } }, ct);

    private static async Task SendRespondAsync(string baseUrl, string rpcId, object result, CancellationToken ct)
    {
        var envelope = new { type = "client-response", rpcId, result };
        DebugLog.Write($"respond rpc={rpcId} result={JsonSerializer.Serialize(result)}");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/respond")
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json")
        };
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        DebugLog.Write($"respond status={(int)resp.StatusCode} body={body[..Math.Min(200, body.Length)]}");
        resp.EnsureSuccessStatusCode();
    }

    // ==================== 会话管理 ====================

    public static async Task<List<HarnessSession>> ListSessionsAsync(AppConfig cfg, CancellationToken ct)
    {
        var baseUrl = BaseUrl(cfg);
        var value = await RpcAsync(baseUrl, "session.list", new { }, ct);
        // 与 web 页面保持一致：归档会话不在列表显示（归档是 workspace 层概念，session.list 不含该信息）
        var archived = await GetArchivedSessionIdsAsync(cfg, ct);
        var result = new List<HarnessSession>();
        if (!value.TryGetProperty("items", out var items)) return result;
        foreach (var it in items.EnumerateArray())
        {
            if (!it.TryGetProperty("sessionId", out var sidEl)) continue;
            var sid = sidEl.GetString() ?? "";
            if (archived.Contains(sid)) continue;
            result.Add(new HarnessSession
            {
                SessionId = sid,
                Title = ReadTitle(it),
                Cwd = it.TryGetProperty("cwd", out var c) ? c.GetString() ?? "" : "",
                UpdatedAt = it.TryGetProperty("updatedAt", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt64() : 0,
                Running = it.TryGetProperty("running", out var run) && run.GetBoolean()
            });
        }
        return result.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    /// <summary>读取已归档的会话 id 集合（workspace.list 的 archivedSessionIds）。失败时返回空集（不过滤）。</summary>
    private static async Task<HashSet<string>> GetArchivedSessionIdsAsync(AppConfig cfg, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var value = await RpcAsync(BaseUrl(cfg), "workspace.list", new { }, ct);
            if (value.TryGetProperty("archivedSessionIds", out var arr))
            {
                foreach (var e in arr.EnumerateArray())
                {
                    var s = e.GetString();
                    if (!string.IsNullOrEmpty(s)) set.Add(s);
                }
            }
        }
        catch { /* 归档信息不可用时不过滤 */ }
        return set;
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
        try
        {
            // 优先挂到「桌面助手」工作区：传 workspaceId 时服务端自动把 cwd 设为工作区路径并 attach 会话
            var wsId = await EnsureWorkspaceAsync(cfg, ct);
            if (wsId.Length > 0)
            {
                var created = await RpcAsync(baseUrl, "session.create", new { workspaceId = wsId }, ct);
                return created.GetProperty("sessionId").GetString() ?? throw new InvalidOperationException("创建会话失败");
            }
        }
        catch { /* 工作区不可用时退回旧逻辑（不影响聊天） */ }
        var created2 = await RpcAsync(baseUrl, "session.create", new { cwd = GroupCwd }, ct);
        return created2.GetProperty("sessionId").GetString() ?? throw new InvalidOperationException("创建会话失败");
    }

    /// <summary>按配置解析会话：指定 ID / "new" 新建 / 留空用最近的「桌面助手」会话。</summary>
    public static async Task<string> ResolveSessionIdAsync(AppConfig cfg, CancellationToken ct)
    {
        var id = cfg.HarnessSessionId?.Trim() ?? "";
        if (id == "new")
        {
            return await CreateSessionAsync(cfg, ct); // 新建：CreateSessionAsync 已挂工作区
        }
        if (id.Length > 0) return id; // 指定会话：不自动改动其所属工作区

        var sessions = await ListSessionsAsync(cfg, ct);
        var inGroup = sessions.Where(InGroup).ToList();
        if (inGroup.Count > 0)
        {
            var sid = inGroup[0].SessionId;
            await EnsureDesktopWorkspaceAsync(cfg, sid, ct); // 已有会话补注册，避免 web 页面显示「未分组」
            return sid;
        }
        return await CreateSessionAsync(cfg, ct);
    }

    /// <summary>确保「桌面助手」工作区存在（路径=桌面目录，名称=桌面助手），返回 workspaceId。</summary>
    private static async Task<string> EnsureWorkspaceAsync(AppConfig cfg, CancellationToken ct)
    {
        var baseUrl = BaseUrl(cfg);
        var desktop = GroupCwd.TrimEnd('\\');
        var list = await RpcAsync(baseUrl, "workspace.list", new { }, ct);
        if (list.TryGetProperty("items", out var items))
        {
            foreach (var w in items.EnumerateArray())
            {
                var path = w.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                var title = w.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                if (!string.Equals(path.TrimEnd('\\'), desktop, StringComparison.OrdinalIgnoreCase) &&
                    title != "桌面助手") continue;
                return w.TryGetProperty("workspaceId", out var id) ? id.GetString() ?? "" : "";
            }
        }
        var created = await RpcAsync(baseUrl, "workspace.create", new { path = GroupCwd }, ct);
        var wsId = created.GetProperty("workspace").GetProperty("workspaceId").GetString() ?? "";
        // 与 DeskBuddy 的「桌面助手」分组命名保持一致
        await RpcAsync(baseUrl, "workspace.rename", new { workspaceId = wsId, title = "桌面助手" }, ct);
        return wsId;
    }

    /// <summary>
    /// 把已有会话注册进「桌面助手」工作区（幂等）。
    /// web 页面按工作区(workspace)分组会话，不在任何工作区的会话会显示在「未分组」下；
    /// 因此 DeskBuddy 的桌面会话要注册进「桌面助手」工作区。
    /// 失败静默（属于非关键路径，不影响聊天本身）。
    /// </summary>
    public static async Task EnsureDesktopWorkspaceAsync(AppConfig cfg, string sessionId, CancellationToken ct)
    {
        try
        {
            var baseUrl = BaseUrl(cfg);
            var wsId = await EnsureWorkspaceAsync(cfg, ct);
            if (wsId.Length == 0) return;
            // 传 workspaceId + 已存在 sessionId：服务端校验 cwd 匹配后自动 attach，重复调用幂等
            await RpcAsync(baseUrl, "session.create", new { workspaceId = wsId, sessionId }, ct);
        }
        catch { /* 非关键路径：注册失败不影响聊天 */ }
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
            if (ProcessFrame(text, observer, ct))
            {
                break; // 回合结束（turn/end），不再继续读流，避免卡住
            }
        }

        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
        catch { }
    }

    /// <summary>处理一帧 WS 文本（server-request 信封）。返回 true 表示回合已结束。</summary>
    private static bool ProcessFrame(string frame, IHarnessObserver observer, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(frame);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() != "server-request") return false;
            var rpcId = root.GetProperty("rpcId").GetString() ?? "";
            var method = root.GetProperty("method").GetString() ?? "";

            if (method == "session/event")
            {
                var ev = root.GetProperty("payload").GetProperty("event");
                return HandleEvent(ev, rpcId, observer);
            }

            // 顶层帧类型（approval/requested、question/requested 等）
            if (!root.TryGetProperty("payload", out var payload)) return false;
            var pt = payload.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (pt == "approval/requested") HandleApproval(payload, rpcId, observer);
            else if (pt == "question/requested") HandleQuestion(payload, rpcId, observer);
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool HandleEvent(JsonElement ev, string rpcId, IHarnessObserver observer)
    {
        if (!ev.TryGetProperty("type", out var t)) return false;
        var type = t.GetString();

        switch (type)
        {
            case "assistant/chunk":
                if (!ev.TryGetProperty("data", out var d) || !d.TryGetProperty("chunk", out var chunk)) return false;
                var chunkType = chunk.TryGetProperty("type", out var ct) ? ct.GetString() : null;
                if (chunkType == "text-delta" && chunk.TryGetProperty("text", out var tx) &&
                    tx.ValueKind == JsonValueKind.String)
                {
                    observer.OnTextDelta(tx.GetString() ?? "");
                }
                else if (chunkType == "reasoning-delta")
                {
                    observer.OnStatus("🧠 思考中…");
                }
                return false;

            case "tool/call":
                var name = ev.TryGetProperty("data", out var d2) && d2.TryGetProperty("name", out var n) ? n.GetString() : "";
                observer.OnStatus(string.IsNullOrEmpty(name) ? "🔧 正在调用工具…" : $"🔧 正在使用工具：{name}");
                return false;

            case "step/start":
                observer.OnStatus("⏳ 开始处理…");
                return false;

            case "approval/requested":
                HandleApproval(ev.TryGetProperty("data", out var ad) ? ad : ev, rpcId, observer);
                return false;

            case "question/requested":
                HandleQuestion(ev.TryGetProperty("data", out var qd) ? qd : ev, rpcId, observer);
                return false;

            case "turn/end":
                DebugLog.Write("turn/end received");
                observer.OnStatus("✅ 完成");
                return true;
        }
        return false;
    }

    private static void HandleApproval(JsonElement data, string rpcId, IHarnessObserver observer)
    {
        if (!data.TryGetProperty("approvalId", out var aid)) return;
        var approval = new HarnessApproval
        {
            RpcId = rpcId,
            SessionId = data.TryGetProperty("sessionId", out var sid) ? sid.GetString() ?? "" : "",
            ApprovalId = aid.GetString() ?? "",
            ToolName = data.TryGetProperty("toolName", out var tn) ? tn.GetString() ?? "" : "",
            Reason = data.TryGetProperty("reason", out var r) ? r.GetString() : null
        };
        DebugLog.Write($"approval/requested: tool={approval.ToolName} session={approval.SessionId} id={approval.ApprovalId} rpc={rpcId}");
        observer.OnApproval(approval);
    }

    private static void HandleQuestion(JsonElement data, string rpcId, IHarnessObserver observer)
    {
        // 注意：一次提问可能包含多个问题（answers 必须全部回答，服务端按数量严格校验）
        if (!data.TryGetProperty("questions", out var qs) || qs.GetArrayLength() == 0) return;
        var sessionId = data.TryGetProperty("sessionId", out var sid) ? sid.GetString() ?? "" : "";
        var list = new List<HarnessQuestion>();
        foreach (var q in qs.EnumerateArray())
        {
            list.Add(new HarnessQuestion
            {
                RpcId = rpcId,
                SessionId = sessionId,
                QuestionId = q.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                Question = q.TryGetProperty("question", out var qq) ? qq.GetString() ?? "（问题）" : "（问题）",
                Header = q.TryGetProperty("header", out var h) ? h.GetString() : null,
                Options = q.TryGetProperty("options", out var opts)
                    ? opts.EnumerateArray().Select(o => o.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "").ToList()
                    : null,
                MultiSelect = q.TryGetProperty("multiSelect", out var ms) && ms.GetBoolean()
            });
        }
        DebugLog.Write($"question/requested: session={sessionId} count={list.Count} rpc={rpcId}");
        observer.OnQuestion(list);
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
