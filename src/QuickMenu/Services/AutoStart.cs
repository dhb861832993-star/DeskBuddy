using Microsoft.Win32;

namespace QuickMenu.Services;

/// <summary>开机自启动（HKCU Run 键，无需管理员权限）。</summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuickMenu";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) != null;
    }

    public static void Set(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enable)
        {
            key.SetValue(ValueName, $"\"{AppContext.BaseDirectory}QuickMenu.exe\"");
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
