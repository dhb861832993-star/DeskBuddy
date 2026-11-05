using System.Diagnostics;
using System.IO;
using System.Text;

namespace DeskBuddy.Services;

/// <summary>
/// 用「计划任务」实现开机静默提权（不弹 UAC）：
/// 任务在用户登录时触发、以 HighestAvailable 权限运行 DeskBuddy.exe，
/// 这样做既拿到了管理员权限，又不用每次启动点 UAC 确认。
///
/// 注意：注册/删除任务本身需要管理员权限；非管理员时通过自提升（runas，弹一次 UAC）完成。
/// </summary>
public static class AdminTask
{
    public const string TaskName = "DeskBuddy";
    /// <summary>自提升时传给自己的参数：注册任务。</summary>
    public const string SetupArg = "--admin-task-setup";
    public const string RemoveArg = "--admin-task-remove";

    /// <summary>DeskBuddy.exe 的完整路径。</summary>
    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "DeskBuddy.exe");

    /// <summary>任务是否已存在。</summary>
    public static bool Exists()
    {
        var (code, _) = Run("schtasks.exe", $"/query /tn \"{TaskName}\"");
        return code == 0;
    }

    /// <summary>已注册的任务是否指向当前 exe（装到别的目录后需要重新注册）。</summary>
    public static bool PointsToCurrentExe()
    {
        var (code, output) = Run("schtasks.exe", $"/query /tn \"{TaskName}\" /xml");
        if (code != 0) return false;
        return output.IndexOf(ExePath, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>注册/更新计划任务（需要管理员）。返回是否成功。</summary>
    public static bool Register()
    {
        try
        {
            var xml = BuildXml();
            var tmp = Path.Combine(Path.GetTempPath(), "deskbuddy_task.xml");
            // schtasks /xml 要求 Unicode 编码
            File.WriteAllText(tmp, xml, new UnicodeEncoding(false, true));
            var (code, output) = Run("schtasks.exe", $"/create /tn \"{TaskName}\" /xml \"{tmp}\" /f");
            try { File.Delete(tmp); } catch { }
            DebugLog.Write($"[ADMIN] 注册计划任务 code={code} {output.Trim()}");
            return code == 0;
        }
        catch (Exception ex)
        {
            DebugLog.Write("[ADMIN] 注册计划任务失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>删除计划任务。</summary>
    public static bool Unregister()
    {
        var (code, output) = Run("schtasks.exe", $"/delete /tn \"{TaskName}\" /f");
        DebugLog.Write($"[ADMIN] 删除计划任务 code={code} {output.Trim()}");
        return code == 0;
    }

    /// <summary>
    /// 非管理员时：以 runas 启动自身的一个短命实例来注册/删除任务（弹一次 UAC）。
    /// </summary>
    public static bool RunElevatedHelper(string arg)
    {
        try
        {
            var psi = new ProcessStartInfo(ExePath, arg) { UseShellExecute = true, Verb = "runas" };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            DebugLog.Write("[ADMIN] 自提升失败（可能点了取消）: " + ex.Message);
            return false;
        }
    }

    /// <summary>进程启动时若带了 --admin-task-setup / --admin-task-remove，处理完直接退出。</summary>
    public static bool HandleStartupArg(string[] args)
    {
        var wantSetup = args.Any(a => a.Equals(SetupArg, StringComparison.OrdinalIgnoreCase));
        var wantRemove = args.Any(a => a.Equals(RemoveArg, StringComparison.OrdinalIgnoreCase));
        if (!wantSetup && !wantRemove) return false;

        var ok = wantSetup ? Register() : Unregister();
        Environment.Exit(ok ? 0 : 1);
        return true;
    }

    private static string BuildXml()
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var dir = AppContext.BaseDirectory.TrimEnd('\\');
        var exe = System.Security.SecurityElement.Escape(ExePath) ?? ExePath;
        var userEsc = System.Security.SecurityElement.Escape(user) ?? user;
        var dirEsc = System.Security.SecurityElement.Escape(dir) ?? dir;
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>DeskBuddy 登录自启（最高权限，无 UAC 提示）</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{userEsc}</UserId>
      <Delay>PT3S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{userEsc}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{exe}</Command>
      <WorkingDirectory>{dirEsc}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static (int Code, string Output) Run(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(15_000);
            return (p.ExitCode, stdout + stderr);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
