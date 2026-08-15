using System.IO;
using System.IO.Pipes;
using System.Text;

namespace DeskBuddy.Services;

/// <summary>
/// 主进程与 MCP 子进程之间的命名管道 IPC（换行分隔的 JSON）。
/// 主进程常驻监听；--mcp 子进程把 AI 工具的请求转发过来，
/// 由主进程统一“读配置 → 修改 → 保存 → 刷新菜单”，避免并发写配置冲突。
/// 使用原始字节读写 + 顺序处理，避免 StreamReader/StreamWriter 在管道上的边界问题。
/// </summary>
public static class McpPipe
{
    public const string PipeName = "DeskBuddy_MCP_v1";

    /// <summary>客户端：发送 JSON 请求并读取 JSON 响应（换行分隔）。连接/通信失败返回 null。</summary>
    public static string? Request(string json, int timeoutMs = 15000)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(timeoutMs);

            var payload = Encoding.UTF8.GetBytes(json + "\n");
            pipe.Write(payload, 0, payload.Length);
            pipe.Flush();

            using var ms = new MemoryStream();
            var buf = new byte[8192];
            while (true)
            {
                var n = pipe.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                ms.Write(buf, 0, n);
                if (ms.GetBuffer()[ms.Length - 1] == (byte)'\n') break; // 收到整行即返回
            }
            return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\n', '\r');
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine($"[DeskBuddy MCP] pipe request failed: {ex.GetType().Name}: {ex.Message}"); } catch { }
            return null;
        }
    }

    /// <summary>服务端：后台线程顺序监听处理每个连接（同一时刻只有一个实例，无并发竞争）。</summary>
    public static void StartServer(Func<string, string> handler, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    await HandleConnectionAsync(pipe, handler).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }, CancellationToken.None);
    }

    private static async Task HandleConnectionAsync(NamedPipeServerStream pipe, Func<string, string> handler)
    {
        try
        {
            var line = await ReadLineAsync(pipe).ConfigureAwait(false);
            DebugLog.Write($"pipe recv: {line}");
            if (line != null)
            {
                var resp = handler(line);
                DebugLog.Write($"pipe resp: {resp}");
                var bytes = Encoding.UTF8.GetBytes(resp + "\n");
                await pipe.WriteAsync(bytes).ConfigureAwait(false);
                await pipe.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"pipe handler error: {ex}");
        }
    }

    private static async Task<string?> ReadLineAsync(Stream s)
    {
        using var ms = new MemoryStream();
        var buf = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(buf).ConfigureAwait(false);
            if (n <= 0) return ms.Length > 0 ? Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\n', '\r') : null;
            if (buf[0] == (byte)'\n') return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\n', '\r');
            ms.WriteByte(buf[0]);
        }
    }
}
