namespace DeskBuddy.Services;

/// <summary>
/// 双击检测器：在指定间隔内同一按键连续按下两次即触发事件。
/// 通过“按下集合”过滤自动重复（按住不放不会触发）。
/// </summary>
public sealed class DoubleTapDetector
{
    private int _vk;
    private int _intervalMs;
    private DateTime _lastDown = DateTime.MinValue;
    private readonly HashSet<int> _down = new();

    /// <summary>检测到双击时触发。</summary>
    public event Action? DoubleTapped;

    public (int Vk, int IntervalMs) Current => (_vk, _intervalMs);

    public DoubleTapDetector(int vk, int intervalMs)
    {
        _vk = vk;
        _intervalMs = intervalMs;
    }

    public void Reconfigure(int vk, int intervalMs)
    {
        _vk = vk;
        _intervalMs = intervalMs;
        _lastDown = DateTime.MinValue;
        _down.Clear();
    }

    public void OnKeyDown(int vkCode)
    {
        vkCode = Normalize(vkCode);
        if (vkCode != _vk) return;
        if (!_down.Add(vkCode)) return; // 已按下 → 自动重复，忽略

        var now = DateTime.UtcNow;
        if ((now - _lastDown).TotalMilliseconds <= _intervalMs)
        {
            DebugLog.Write("DOUBLE TAP DETECTED!");
            _lastDown = DateTime.MinValue;
            DoubleTapped?.Invoke();
        }
        else
        {
            _lastDown = now;
        }
    }

    public void OnKeyUp(int vkCode)
    {
        vkCode = Normalize(vkCode);
        if (vkCode == _vk) _down.Remove(vkCode);
    }

    /// <summary>把左右修饰键变体归一化为泛化虚拟键（钩子报告 0xA2/0xA3 而配置用 0x11 等）。</summary>
    private static int Normalize(int vk) => vk switch
    {
        0xA0 or 0xA1 => 0x10, // L/R Shift
        0xA2 or 0xA3 => 0x11, // L/R Ctrl
        0xA4 or 0xA5 => 0x12, // L/R Alt
        0x5B or 0x5C => 0x5B, // L/R Win
        _ => vk
    };
}
