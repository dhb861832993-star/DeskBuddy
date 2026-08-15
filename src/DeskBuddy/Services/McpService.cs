using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DeskBuddy.Services;

/// <summary>
/// DeskBuddy 的 MCP 服务（stdio）：以 <c>DeskBuddy.exe --mcp</c> 启动，
/// 供任意支持 MCP 的 AI 工具（Claude Desktop / Cursor / Cherry Studio 等）连接。
/// 工具实现经命名管道转发给主进程执行，保证配置读写一致。
/// </summary>
public static class McpService
{
    /// <summary>启动 MCP 服务，直到客户端断开或取消。</summary>
    public static async Task RunAsync(CancellationToken ct)
    {
        // 开关关闭时直接拒绝（写 stderr 不污染 stdio 协议）
        var cfg = ConfigManager.Load();
        if (!cfg.McpEnabled)
        {
            Console.Error.WriteLine("DeskBuddy MCP 未启用：请在 DeskBuddy 设置中开启「AI 快捷添加（MCP）」");
            return;
        }

        // 确保主进程在运行（管道监听方），否则先拉起它
        EnsureMainProcessRunning();

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "DeskBuddy", Version = "1.8.9" },
            ServerInstructions = "DeskBuddy 快捷菜单 MCP 服务：AI 工具可以查看（list_menu_items）、添加（add_menu_item）、删除（remove_menu_item）本机 DeskBuddy 的快捷菜单条目。添加/删除立即生效，无需重启。",
        };
        options.ToolCollection = new McpServerPrimitiveCollection<McpServerTool>
        {
            McpServerTool.Create(AddMenuItem, new McpServerToolCreateOptions
            {
                Name = "add_menu_item",
                Description = "向 DeskBuddy 快捷菜单添加一个条目并立即生效。type 取值：app(程序)/url(网页)/folder(文件夹)/file(文件)/command(命令)。path 为程序路径/网址/文件夹/文件/命令；args/keywords 可选；icon 可选自定义图标文件路径(.ico/.png/.exe)。同名条目会失败，可先 list_menu_items 查重。",
            }),
            McpServerTool.Create(RemoveMenuItem, new McpServerToolCreateOptions
            {
                Name = "remove_menu_item",
                Description = "按名称删除一个 DeskBuddy 快捷菜单条目并立即生效。删除前建议先 list_menu_items 确认名称。",
            }),
            McpServerTool.Create(ListMenuItems, new McpServerToolCreateOptions
            {
                Name = "list_menu_items",
                Description = "列出 DeskBuddy 快捷菜单的所有条目（name/type/path/args/keywords/icon）。",
                ReadOnly = true,
            }),
        };

        // 官方推荐的 Hosting 组装方式：WithStdioServerTransport 注册 stdin/stdout 传输，
        // host.RunAsync 启动后自动读取 stdin（stdin 关闭时服务自动退出）
        var host = Host.CreateApplicationBuilder();
        host.Services.AddMcpServer(o =>
        {
            o.ServerInfo = options.ServerInfo;
            o.ServerInstructions = options.ServerInstructions;
        })
        .WithStdioServerTransport()
        .WithTools(options.ToolCollection);
        await host.Build().RunAsync(ct);
    }

    // ==================== 工具（转发给主进程） ====================

    private static string AddMenuItem(string name, string type, string path, string? args = null, string? keywords = null, string? icon = null)
    {
        var req = JsonSerializer.Serialize(new
        {
            op = "add",
            item = new { name, type, path, args, keywords, icon }
        });
        return McpPipe.Request(req) ?? McpPipeDown();
    }

    private static string RemoveMenuItem(string name)
    {
        var req = JsonSerializer.Serialize(new { op = "remove", name });
        return McpPipe.Request(req) ?? McpPipeDown();
    }

    private static string ListMenuItems()
    {
        var req = JsonSerializer.Serialize(new { op = "list" });
        return McpPipe.Request(req) ?? McpPipeDown();
    }

    private static string McpPipeDown() =>
        "{\"ok\":false,\"message\":\"无法连接 DeskBuddy 主程序（请确认 DeskBuddy 已启动，且设置中已开启 AI 快捷添加）\"}";

    /// <summary>主进程没在运行（或管道还没就绪）时，先启动主程序并等待管道可用（最多约 3 秒）。</summary>
    private static void EnsureMainProcessRunning()
    {
        for (var i = 0; i < 10; i++)
        {
            if (McpPipe.Request("{\"op\":\"ping\"}", 500) != null) return;
            if (i == 0 && Environment.ProcessPath is { } exe)
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
                }
                catch { }
            }
            Thread.Sleep(250);
        }
    }
}
