using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QuickMenu.Services;

/// <summary>
/// 全局低级键盘钩子（WH_KEYBOARD_LL），用于监听任意前台程序中的按键。
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _disposed;

    /// <summary>虚拟键按下（vkCode）。</summary>
    public event Action<int>? KeyDown;

    /// <summary>虚拟键抬起（vkCode）。</summary>
    public event Action<int>? KeyUp;

    public KeyboardHook()
    {
        // 委托必须保存在字段中，防止被 GC 回收
        _proc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
        DebugLog.Write($"hook installed: handle={_hookId} err={Marshal.GetLastWin32Error()}");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                DebugLog.Write($"KEYDOWN vk=0x{kbd.vkCode:X2}");
                KeyDown?.Invoke(kbd.vkCode);
            }
            else if (msg is WM_KEYUP or WM_SYSKEYUP)
            {
                DebugLog.Write($"KEYUP   vk=0x{kbd.vkCode:X2}");
                KeyUp?.Invoke(kbd.vkCode);
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }
}
