using Microsoft.Win32;

namespace DeskBuddy.Services;

/// <summary>
/// 开机自启动。两种方式：
/// ① 普通：HKCU\...\Run 写一条（普通权限，登录时静默启动但不是管理员）；
/// ② 管理员：交给 <see cref="AdminTask"/> 的计划任务（HighestAvailable，登录时静默提权，不弹 UAC）。
/// 两者互斥，避免重复启动。
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeskBuddy";

    /// <summary>是否已配置为开机自启（两种方式任一存在）。</summary>
    public static bool IsEnabled() => IsRunKeyEnabled() || AdminTask.Exists();

    public static bool IsRunKeyEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) != null;
    }

    /// <summary>当前是不是走「管理员计划任务」这条路。</summary>
    public static bool IsAdminMode() => AdminTask.Exists();

    /// <summary>
    /// 应用自启设置。<paramref name="asAdmin"/> 指定是否用计划任务提权启动。
    /// 返回是否成功；失败时 <paramref name="error"/> 给出可读原因。
    /// </summary>
    public static bool Set(bool enable, bool asAdmin, out string error)
    {
        error = "";
        if (!enable)
        {
            SetRunKey(false);
            if (AdminTask.Exists()) AdminTask.Unregister();
            return true;
        }

        if (!asAdmin)
        {
            // 普通自启：先清掉可能存在的计划任务，再写 Run 键
            if (AdminTask.Exists()) AdminTask.Unregister();
            SetRunKey(true);
            return true;
        }

        // 管理员自启：注册计划任务（需要管理员，没权限就弹一次 UAC）
        bool ok;
        if (Launcher.IsElevated || AdminTask.Exists())
            ok = AdminTask.Register();
        else
            ok = AdminTask.RunElevatedHelper(AdminTask.SetupArg);

        if (!ok)
        {
            error = "注册计划任务失败（可能取消了 UAC 授权）。已保持原来的普通自启方式。";
            return false;
        }
        SetRunKey(false);   // 交给计划任务，避免登录时启动两份
        return true;
    }

    /// <summary>首次安装/升级后：任务指向旧路径时重新注册一次。</summary>
    public static void EnsureTaskPathCurrent()
    {
        try
        {
            if (!AdminTask.Exists() || AdminTask.PointsToCurrentExe()) return;
            if (Launcher.IsElevated) AdminTask.Register();
            else AdminTask.RunElevatedHelper(AdminTask.SetupArg);
        }
        catch (Exception ex) { DebugLog.Write("[ADMIN] 校正任务路径失败: " + ex.Message); }
    }

    private static void SetRunKey(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enable) key.SetValue(ValueName, $"\"{AdminTask.ExePath}\"");
        else key.DeleteValue(ValueName, false);
    }
}
