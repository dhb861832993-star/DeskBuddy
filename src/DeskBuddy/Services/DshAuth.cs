using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskBuddy.Services;

/// <summary>
/// DSH web 的浏览器会话鉴权：用 ~/.dsh/.credentials.yaml 里的 browser-session secret
/// 自签 HMAC-SHA256 Cookie（与 dsh web 进程同源校验），有效期 29 天。
/// v2 协议（2026-10 起 /api/* 强制鉴权后必须携带）。
/// </summary>
public static class DshAuth
{
    private static CookieContainer? _container;
    private static string? _cookieForAuthority;
    private static readonly object Lock = new();

    /// <summary>取带鉴权的 HttpClientHandler（CookieContainer 注入签名 Cookie）。</summary>
    public static HttpClientHandler AuthedHandler(string baseUrl)
    {
        var authority = AuthorityOf(baseUrl);
        var cookie = BuildCookie(authority);
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false,
        };
        var host = HostOf(baseUrl);
        handler.CookieContainer.Add(new Uri($"http://{host}/"), new Cookie(cookie.Name, cookie.Value, "/", HostOnly(host)));
        return handler;
    }

    /// <summary>给现有请求头直接附 Cookie（不重建 HttpClient 时用）。</summary>
    public static void Attach(HttpRequestMessage req, string baseUrl)
    {
        req.Headers.Add("Cookie", CookieHeader(baseUrl));
    }

    /// <summary>生成完整 Cookie 请求头值（"name=value"）。</summary>
    public static string CookieHeader(string baseUrl)
    {
        var authority = AuthorityOf(baseUrl);
        var cookie = BuildCookie(authority);
        return $"{cookie.Name}={cookie.Value}";
    }

    private static (string Name, string Value) BuildCookie(string authority)
    {
        var secret = LoadSecret();
        if (secret == null) throw new InvalidOperationException(
            "未找到 DSH 凭据（~/.dsh/.credentials.yaml 无 browser-session）。请先用 dsh web 启动并打开一次页面。");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = $"{{\"issuedAt\":{now},\"authority\":\"{authority}\",\"version\":1,\"expiresAt\":{now + 29L * 24 * 60 * 60 * 1000}}}";
        var bodyB64 = B64Url(Encoding.UTF8.GetBytes(payload));
        var sig = B64Url(new HMACSHA256(secret).ComputeHash(Encoding.UTF8.GetBytes(bodyB64)));
        var name = "dsh-auth-" + B64Url(SHA256.HashData(Encoding.UTF8.GetBytes(authority)));
        return (name, $"v1.{bodyB64}.{sig}");
    }

    /// <summary>读 ~/.dsh/.credentials.yaml 的 client-connection/browser-session secret。</summary>
    private static byte[]? LoadSecret()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", ".credentials.yaml");
        if (!File.Exists(path)) return null;
        try
        {
            // 简析 YAML：找 records 下 client-connection/browser-session 的 secret 行
            var lines = File.ReadAllLines(path);
            string? secretB64 = null;
            for (int i = 0; i < lines.Length - 1; i++)
            {
                if (lines[i].Trim() == "browser-session:" && lines[i].Contains("  ") is false) continue;
                if (lines[i].Contains("browser-session"))
                {
                    // 其后 6 行内找 "    secret: xxx"
                    for (int j = i + 1; j < Math.Min(i + 8, lines.Length); j++)
                    {
                        var t = lines[j].Trim();
                        if (t.StartsWith("secret:"))
                        {
                            secretB64 = t["secret:".Length..].Trim().Trim('"');
                            break;
                        }
                        if (t.Length > 0 && !lines[j].StartsWith("      ") && !t.StartsWith("kind:") && !t.StartsWith("payload:") && !t.StartsWith("version:"))
                            if (t.EndsWith(":")) break; // 进入下个 record
                    }
                    if (secretB64 != null) break;
                }
            }
            if (secretB64 == null) return null;
            var s = secretB64.Replace('-', '+').Replace('_', '/');
            s += new string('=', (4 - s.Length % 4) % 4);
            return Convert.FromBase64String(s);
        }
        catch { return null; }
    }

    private static string AuthorityOf(string baseUrl)
    {
        try
        {
            var uri = new Uri(baseUrl);
            return uri.Authority;   // host:port
        }
        catch { return "127.0.0.1:3080"; }
    }

    private static string HostOf(string baseUrl)
    {
        try { return new Uri(baseUrl).Host; } catch { return "127.0.0.1"; }
    }

    private static string HostOnly(string host) => host;

    private static string B64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}