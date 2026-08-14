using System.IO;

namespace QuickMenu.Services;

/// <summary>调试日志（设置环境变量 QM_DEBUG=1 时启用），用于排查热键钩子问题。</summary>
public static class DebugLog
{
    public static bool Enabled => Environment.GetEnvironmentVariable("QM_DEBUG") == "1";

    private static readonly object Sync = new();

    public static void Write(string msg)
    {
        if (!Enabled) return;
        lock (Sync)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "qm_debug.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
