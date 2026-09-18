using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace CheatSheet;

/// <summary>
/// 基于 Win32 <c>RegisterHotKey</c> 的全局热键管理器。
/// <para>
/// 这是"边玩游戏边看攻略"的关键:热键注册在系统级,即使本窗口失去焦点、
/// 游戏全屏置于最前,按键依然会被系统派发到这里。
/// </para>
/// <para>
/// 用户在设置里改键后,<see cref="MainWindow"/> 会整体换一个新的实例
/// (旧实例 Dispose,新实例重新注册),这样不需要维护"逐个改键"的复杂状态。
/// </para>
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    /// <summary>按住不放时只触发一次,避免连发。</summary>
    private const uint MOD_NOREPEAT = 0x4000;

    // GetAsyncKeyState 用的虚拟键码
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>取按键当前是否按着(最高位为 1 表示按下),用于判断热键有没有松开。</summary>
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private readonly List<int> _registeredIds = new();
    private int _nextId = 1;
    private bool _disposed;

    /// <summary>注册失败(通常是被别的程序占用)的热键,用于给用户提示。</summary>
    public List<string> FailedHotkeys { get; } = new();

    public HotkeyManager(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd)
                  ?? throw new InvalidOperationException("无法获取窗口消息源,全局热键不可用。");

        _source.AddHook(WndProc);
    }

    /// <summary>
    /// 注册一个全局热键。手势写成 <c>"Ctrl+Alt+Space"</c> 这样的形式,
    /// 修饰键支持 Ctrl / Alt / Shift / Win,最后一段是 <see cref="Key"/> 名称。
    /// </summary>
    /// <returns>注册成功返回 true;被占用返回 false 并记入 <see cref="FailedHotkeys"/>。</returns>
    public bool Register(string gesture, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (!TryParseGesture(gesture, out uint modifiers, out uint virtualKey))
            throw new ArgumentException($"无法解析快捷键手势: {gesture}", nameof(gesture));

        int id = _nextId++;

        if (!RegisterHotKey(_hwnd, id, modifiers | MOD_NOREPEAT, virtualKey))
        {
            FailedHotkeys.Add(gesture);
            return false;
        }

        _registeredIds.Add(id);
        _handlers[id] = callback;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out Action? action))
        {
            action();
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>校验一个手势字符串能否被解析成合法的全局热键。</summary>
    public static bool TryParseGesture(string gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(gesture))
            return false;

        string[] parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];

            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= MOD_ALT;
                    break;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    break;
                case "win":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    // 最后一段必须是真正的按键;修饰键位置放错就直接判定非法。
                    if (i != parts.Length - 1 || !Enum.TryParse(part, ignoreCase: true, out Key key))
                        return false;

                    virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }

        return virtualKey != 0;
    }

    /// <summary>
    /// 把"按下的修饰键 + 主键"拼成手势字符串,供设置面板里的录制功能使用。
    /// <para>
    /// 返回 null 只表示这次按键不能当热键(比如只按了 Ctrl 本身)。
    /// 允许不带修饰键的单键(如 <c>F5</c>)—— 只是要在界面上提醒用户:单键会被全局抢走。
    /// </para>
    /// </summary>
    public static string? GestureFrom(ModifierKeys modifiers, Key key)
    {
        if (key is Key.None
            or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin
            or Key.System or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return null;
        }

        var parts = new List<string>(4);

        if (modifiers.HasFlag(ModifierKeys.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows))
            parts.Add("Win");

        parts.Add(key.ToString());
        return string.Join('+', parts);
    }

    /// <summary>
    /// 手势的"给人看的写法":D1 → 1、OemComma → ,、Up → ↑……
    /// <para>
    /// 只用于界面显示 —— 存进配置、拿去注册的始终是 <see cref="Key"/> 的名字
    /// (否则解析不回去,热键就注册不上了)。
    /// </para>
    /// </summary>
    public static string FriendlyGesture(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
            return gesture;

        string[] parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
            return gesture;

        parts[^1] = FriendlyKeyName(parts[^1]);
        return string.Join('+', parts);
    }

    private static string FriendlyKeyName(string name) => name switch
    {
        "Up" => "↑",
        "Down" => "↓",
        "Left" => "←",
        "Right" => "→",
        "Space" => "空格",
        "Enter" => "回车",
        "Escape" => "Esc",
        "Back" => "退格",
        "Tab" => "Tab",
        "Prior" => "PgUp",
        "Next" => "PgDn",
        "Capital" => "CapsLock",
        "OemMinus" => "-",
        "OemPlus" => "=",
        "OemComma" => ",",
        "OemPeriod" => ".",
        "OemQuestion" => "/",
        "OemSemicolon" => ";",
        "OemQuotes" => "'",
        "OemTilde" => "`",
        "OemPipe" => "\\",
        "OemOpenBrackets" => "[",
        "OemCloseBrackets" => "]",
        _ when name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]) => name[1].ToString(),
        _ => name
    };

    /// <summary>
    /// 判断某个手势对应的按键现在是否还按着。全局热键只有"按下"事件(WM_HOTKEY),
    /// 没有"松开"事件,所以"按住加速、松开恢复"这类功能只能靠轮询它。
    /// </summary>
    public static bool IsGestureHeld(string gesture)
    {
        if (!TryParseGesture(gesture, out uint modifiers, out uint virtualKey))
            return false;

        if ((GetAsyncKeyState((int)virtualKey) & 0x8000) == 0)
            return false;

        return ModifierHeld(modifiers, MOD_CONTROL, VK_CONTROL)
               && ModifierHeld(modifiers, MOD_ALT, VK_MENU)
               && ModifierHeld(modifiers, MOD_SHIFT, VK_SHIFT)
               && ModifierHeld(modifiers, MOD_WIN, VK_LWIN);
    }

    private static bool ModifierHeld(uint modifiers, uint flag, int virtualKey)
        => (modifiers & flag) == 0 || (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (int id in _registeredIds)
            UnregisterHotKey(_hwnd, id);

        _registeredIds.Clear();
        _handlers.Clear();
        _source.RemoveHook(WndProc);
    }
}
