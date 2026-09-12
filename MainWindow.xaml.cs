using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using HandyControl.Data;
using Microsoft.Win32;
using Growl = HandyControl.Controls.Growl;

namespace CheatSheet;

/// <summary>
/// CheatSheet 的播放窗口:整块就是画面,进度条浮在画面底部。
/// <para>
/// 操作在屏幕正上方那条独立的 <see cref="ControlBarWindow"/> 上,设置在自己的
/// <see cref="SettingsWindow"/> 里;三者通过 <see cref="StateChanged"/> 事件 + 一组
/// internal 命令保持同步。窗口本身全透明,透明度只作用在画面上,所以半透明时能透出后面的游戏。
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] VideoExtensions =
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm",
        ".flv", ".ts", ".mpg", ".mpeg", ".3gp", ".rmvb"
    };

    private static readonly double[] PlaybackSpeeds = { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };

    /// <summary>按住快进时使用的倍速。</summary>
    private const double HoldSpeedRatio = 2.0;

    /// <summary>按住多久算"长按"(超过它才算按住,否则算点按)。</summary>
    private const int HoldDelayMs = 220;

    private readonly AppSettings _settings;
    private readonly List<string> _playlist = new();
    private readonly List<HotkeyAction> _hotkeyActions;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _holdTimer;

    private ControlBarWindow? _controlBar;
    private SettingsWindow? _settingsWindow;
    private HotkeyManager? _hotkeys;
    private int _index = -1;
    private double _volumePercent = 80;
    private double _speedRatio = 1.0;
    private string _fileLabel = "把视频文件拖到这里开始播放";
    private bool _ready;
    private bool _isPlaying;
    private bool _muted;
    private bool _suppressProgressEvent;
    private bool _isScrubbing;
    private bool _syncing;
    private bool _dragCandidate;
    private Point _dragOrigin;
    private double _lastNonZeroVolume = 80;
    private string? _heldGesture;
    private bool _heldByButton;

    /// <summary>当前画面变暗是"暂停压暗"造成的(不是用户自己调的值)。</summary>
    private bool _pausedDimmed;

    /// <summary>当前窗口隐藏是"暂停时隐藏"造成的(不是用户按热键藏的)。</summary>
    private bool _pausedHidden;

    /// <summary>窗口是不是被 Ctrl+Alt+T 藏起来的。用它判断而不是 WindowState ——
    /// 隐藏走的是 SW_HIDE,窗口状态不再是最小化。</summary>
    private bool _hiddenByHotkey;
    private double _speedBeforeHold = 1.0;
    private DateTime _seekHoldStart;
    private bool _seekHoldIsLongPress;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();

        // 保留的全局热键:播放、快退、快进、显示/隐藏窗口、透明度 ±。
        // 快退/快进走 BeginSeekHold —— 点一下跳 5 秒,按住则是 2 倍速播放。
        // 全部都包在 RunHotkey 里 —— 它会保证动作执行完把前台还给游戏。
        _hotkeyActions = new List<HotkeyAction>
        {
            new("PlayPause", "播放 / 暂停", "Ctrl+Alt+Space", () => RunHotkey(TogglePlayPause)),
            new("SeekBack", "快退(按住 2 倍速)", "Ctrl+Alt+Left", () => RunHotkey(() => BeginSeekHold("SeekBack", -1))),
            new("SeekForward", "快进(按住 2 倍速)", "Ctrl+Alt+Right", () => RunHotkey(() => BeginSeekHold("SeekForward", 1))),
            new("WindowVisible", "显示 / 隐藏播放窗口", "Ctrl+Alt+T", () => RunHotkey(ToggleWindowVisible)),
            new("OpacityDown", "透明度 -", "Ctrl+Alt+Z", () => RunHotkey(() => ChangeOpacity(-10))),
            new("OpacityUp", "透明度 +", "Ctrl+Alt+X", () => RunHotkey(() => ChangeOpacity(10))),
        };

        ApplySettings();
        RestoreHotkeyGestures();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) =>
        {
            RefreshProgress();
            UpdatePauseBehavior();
        };

        // 全局热键只有"按下"事件(WM_HOTKEY),所以靠轮询 GetAsyncKeyState 判断是否松开。
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _holdTimer.Tick += (_, _) => CheckSeekHold();

        _ready = true;

        // 用持久化的值把界面拉到一致状态。
        ApplyVolume(_settings.Volume * 100.0);
        ApplyOpacity(_settings.WindowOpacity * 100.0);
        ApplySpeed(SpeedToIndex(_settings.SpeedRatio));

        // 播放窗口换屏 / 移动时,顶上那条跟着挪过去。
        LocationChanged += (_, _) => _controlBar?.Reposition();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ---------------- 控制条 / 设置窗口 ↔ 播放窗口 ----------------

    /// <summary>控制条与设置窗口订阅这个事件来刷新自己的界面(与 <see cref="Window.StateChanged"/> 无关,故用 new 隐藏)。</summary>
    internal new event EventHandler? StateChanged;

    internal bool IsPlaying => _isPlaying;

    internal bool IsMuted => _muted;

    internal double VolumePercent => _volumePercent;

    internal int SpeedIndex => GetSpeedIndex();

    internal string SpeedLabel => $"{GetSpeed():0.##}x";

    internal string FileLabel => _fileLabel;

    /// <summary>画面透明度(30–100),设置窗口用它显示滑块。</summary>
    internal double VideoOpacityPercent => VideoArea.Opacity * 100.0;

    internal IReadOnlyList<HotkeyAction> HotkeyActions => _hotkeyActions;

    /// <summary>拖动窗口边缘时是否按视频比例等比例缩放(设置窗口里的开关)。</summary>
    internal bool ProportionalResize
    {
        get => _settings.ProportionalResize;
        set
        {
            if (_settings.ProportionalResize == value)
                return;

            _settings.ProportionalResize = value;
            _settings.Save();
        }
    }

    internal void TogglePlayPause()
    {
        if (!_ready)
            return;

        // 还没打开任何文件时什么都不做。
        // (以前这里会直接弹文件对话框"少一次点击",但把播放键绑成单键 Up 之后,
        //  按它就弹打开文件,很容易让人误以为这个键的功能是"打开文件"。)
        if (Player.Source is null)
            return;

        if (_isPlaying)
        {
            Player.Pause();
            _isPlaying = false;
        }
        else
        {
            Player.Play();
            _isPlaying = true;
        }

        UpdatePauseBehavior();
        RaiseStateChanged();
    }

    /// <summary>
    /// 按设置把"暂停时"的效果加上/撤掉。轮询调用,所以状态判断都写成幂等的。
    /// </summary>
    private void UpdatePauseBehavior()
    {
        if (!_ready || Player is null)
            return;

        // 没打开视频时不算"暂停" —— 否则程序一启动(还没播任何东西)就会把画面压暗,
        // 选"隐藏视频窗口"的话更狠:窗口直接不见。
        bool paused = Player.Source is not null && !_isPlaying;
        int behavior = _settings.PauseBehavior;

        if (paused && behavior == 1 && !_pausedHidden)
        {
            // 用 SW_HIDE 而不是最小化:不动前台,游戏察觉不到。
            _pausedHidden = true;
            ShowWindow(new WindowInteropHelper(this).Handle, SW_HIDE);
        }
        else if (!paused && _pausedHidden)
        {
            _pausedHidden = false;
            ShowWindow(new WindowInteropHelper(this).Handle, SW_SHOWNOACTIVATE);
        }

        if (paused && behavior == 2 && !_pausedDimmed)
        {
            _pausedDimmed = true;
            ApplyOpacity(30);   // 30 = 设置里滑块的下限,也是 ApplyOpacity 自己的下限
        }
        else if ((!paused || behavior != 2) && _pausedDimmed)
        {
            _pausedDimmed = false;
            ApplyOpacity(_settings.WindowOpacity * 100.0);
        }
    }

    /// <summary>撤掉"暂停压暗 / 暂停隐藏"的临时效果,把画面和窗口都还原。</summary>
    private void ResetPauseEffects()
    {
        if (_pausedDimmed)
        {
            _pausedDimmed = false;
            ApplyOpacity(_settings.WindowOpacity * 100.0);
        }

        if (_pausedHidden)
        {
            _pausedHidden = false;
            ShowWindow(new WindowInteropHelper(this).Handle, SW_SHOWNOACTIVATE);
        }
    }

    internal void PlayPrevious() => PlayAt(_index - 1);

    internal void PlayNext() => PlayAt(_index + 1);

    internal void SetVolume(double percent) => ApplyVolume(percent);

    internal void SetSpeedIndex(int index) => ApplySpeed(index);

    internal void SetOpacity(double percent)
    {
        // 记下用户真正想要的透明度。暂停时的临时压暗不能写进这里,
        // 否则下次启动会以为用户要的就是那个暗值。
        _settings.WindowOpacity = Math.Clamp(percent, 30, 100) / 100.0;
        _pausedDimmed = false;
        ApplyOpacity(percent);
    }

    /// <summary>
    /// 暂停时怎么处理:0 = 什么都不做,1 = 隐藏窗口,2 = 把画面压暗到最低。
    /// </summary>
    internal int PauseBehavior
    {
        get => _settings.PauseBehavior;
        set
        {
            if (_settings.PauseBehavior == value)
                return;

            _settings.PauseBehavior = value;

            // 换了方案先把上一个方案的效果撤掉,再按新方案来。
            ResetPauseEffects();
            UpdatePauseBehavior();
            RaiseStateChanged();
        }
    }

    internal void CycleSpeed() => ApplySpeed((GetSpeedIndex() + 1) % PlaybackSpeeds.Length);

    internal void ToggleMute()
    {
        if (!_ready)
            return;

        if (_volumePercent > 0.5)
        {
            _lastNonZeroVolume = _volumePercent;
            ApplyVolume(0);
        }
        else
        {
            ApplyVolume(_lastNonZeroVolume <= 0.5 ? 80 : _lastNonZeroVolume);
        }
    }

    /// <summary>
    /// 热键动作的统一入口:执行前后台是谁,执行完就还给谁。
    /// <para>
    /// 按热键本来不该动前台,但有些动作会顺手把我们的窗口激活(或弹出对话框),
    /// 结果就是"按完热键还得再点一下游戏才能用键盘"。这里统一兜住。
    /// 只还"别的进程"的窗口,所以自己家窗口之间切换不受影响。
    /// </para>
    /// </summary>
    private void RunHotkey(Action action)
    {
        IntPtr previous = GetForegroundWindow();

        try
        {
            action();
        }
        finally
        {
            GiveBackFocus(previous);
        }
    }

    /// <summary>显示 / 隐藏播放窗口(隐藏用最小化,这样任务栏还找得回来)。</summary>
    internal void ToggleWindowVisible()
    {
        // 一律走 Win32,而且都是"不激活"的版本。
        // <para>
        // 以前用 WindowState = Minimized/Normal:恢复最小化窗口时 Windows 会把前台抢过去,
        // 游戏收到 WM_KILLFOCUS 就把按键状态清了 —— 按住 A 走路时切一下窗口,人就走不动了,
        // 得重按一次。还焦点也救不回来,因为"被抢"那一刻游戏已经收到了。
        // SW_HIDE / SW_SHOWNOACTIVATE 则压根不动前台。
        // </para>
        IntPtr hwnd = new WindowInteropHelper(this).Handle;

        if (_hiddenByHotkey)
        {
            _hiddenByHotkey = false;
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        }
        else
        {
            _hiddenByHotkey = true;
            ShowWindow(hwnd, SW_HIDE);
        }

        RaiseStateChanged();
    }

    /// <summary>控制条上的 -5s / +5s:按下开始,松开结束。</summary>
    internal void BeginSeekHoldByButton(int direction)
    {
        _heldByButton = true;
        BeginSeekHold(direction < 0 ? "SeekBack" : "SeekForward", direction);
    }

    internal void EndSeekHoldByButton()
    {
        _heldByButton = false;
        EndSeekHold();
    }

    /// <summary>
    /// 快退 / 快进的统一起点。
    /// <para>
    /// 快退:按下就直接跳一步 —— MediaElement 不能倒放(负倍速),做不了"快速倒退",所以不做长按加速。
    /// 快进:按下先不跳 —— 快速点按才跳 5 秒,按住超过 <see cref="HoldDelayMs"/> 才切 2 倍速播放。
    /// </para>
    /// </summary>
    private void BeginSeekHold(string actionKey, int direction)
    {
        if (!_ready)
            return;

        // 上一轮可能还没收尾(比如松手后轮询还没轮到)。这里先把它收干净,
        // 而不是直接把这次按键吞掉 —— 以前那样会导致"长按过一次之后快进就没反应"。
        if (_holdTimer.IsEnabled)
            EndSeekHold();

        if (direction < 0)
        {
            Seek(-_settings.SeekStepSeconds);
            return;
        }

        _seekHoldStart = DateTime.UtcNow;
        _seekHoldIsLongPress = false;

        // 手势从动作表里取,这样用户在设置里改过键也能正确判断"松开"。
        _heldGesture = _hotkeyActions.FirstOrDefault(a => a.Key == actionKey)?.Gesture;
        _speedBeforeHold = GetSpeed();

        _holdTimer.Start();
    }

    /// <summary>
    /// 收尾:恢复倍速 + 清状态。写成幂等的 —— 以前开头有个
    /// "定时器没在跑就直接 return",一旦走到那条路,倍速就卡在 2 倍回不来了。
    /// </summary>
    private void EndSeekHold()
    {
        _holdTimer.Stop();
        _heldGesture = null;
        _heldByButton = false;

        if (!_seekHoldIsLongPress)
            return;

        _seekHoldIsLongPress = false;
        SetPlaybackSpeed(_speedBeforeHold);
    }

    /// <summary>轮询:判断有没有松开,同时把"长按快进"变成 2 倍速。</summary>
    private void CheckSeekHold()
    {
        bool stillHeld = _heldByButton
            ? Mouse.LeftButton == MouseButtonState.Pressed
            : _heldGesture is not null && HotkeyManager.IsGestureHeld(_heldGesture);

        if (!stillHeld)
        {
            // 没到长按阈值就松开 = 点按,这时候才跳那一步;没在播放时也按点按处理。
            bool wasTap = !_seekHoldIsLongPress || !_isPlaying;

            _heldByButton = false;
            EndSeekHold();

            if (wasTap)
                Seek(_settings.SeekStepSeconds);

            return;
        }

        if (_seekHoldIsLongPress || (DateTime.UtcNow - _seekHoldStart).TotalMilliseconds < HoldDelayMs)
            return;

        _seekHoldIsLongPress = true;

        if (_isPlaying)
            SetPlaybackSpeed(HoldSpeedRatio);
    }

    private void SetPlaybackSpeed(double ratio)
    {
        if (Player is not null)
            Player.SpeedRatio = ratio;
    }

    /// <summary>打开独立的设置窗口;已经开着就把它拿到前面来。</summary>
    internal void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        IntPtr previous = GetForegroundWindow();

        var window = new SettingsWindow(this);
        window.Closed += (_, _) =>
        {
            _settingsWindow = null;
            GiveBackFocus(previous);
        };
        _settingsWindow = window;
        window.Show();
    }

    /// <summary>
    /// 录制快捷键期间先把全局热键全部注销。否则按到一个已经绑定的组合时,
    /// 会被系统当成热键派发出去(执行那个功能),根本录不进来。
    /// </summary>
    internal void SuspendHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
    }

    /// <summary>改完快捷键后:重新注册 + 立即落盘。</summary>
    internal void CommitHotkeyChange()
    {
        ApplyHotkeys();
        SyncHotkeysToSettings();
        _settings.Save();
    }

    internal void ResetHotkeys()
    {
        foreach (HotkeyAction action in _hotkeyActions)
            action.Gesture = action.DefaultGesture;

        CommitHotkeyChange();
    }

    // ---------------- 生命周期 ----------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 分层透明窗口在构造阶段设 Width/Height 有时不生效(会被顶到 MinWidth/MinHeight),
        // Show 之后再应用一次就稳了。
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);

        ApplyHotkeys();

        // 支持命令行直接带视频启动(把文件拖到 CheatSheet.exe 上、或设置"打开方式"关联)
        var files = Environment.GetCommandLineArgs()
            .Skip(1)
            .Where(File.Exists)
            .Where(IsVideoFile)
            .ToList();

        if (files.Count > 0)
        {
            _settings.LastDirectory = Path.GetDirectoryName(files[0]);
            StartPlaylist(files, 0);
        }

        // 屏幕正上方那条独立控制条。
        _controlBar = new ControlBarWindow(this);
        _controlBar.Show();
        _controlBar.Reposition();

        RaiseStateChanged();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _timer.Stop();
        _holdTimer.Stop();
        _hotkeys?.Dispose();
        SaveSettings();

        _settingsWindow?.Close();
        _settingsWindow = null;

        if (_controlBar is not null)
        {
            _controlBar.Close();
            _controlBar = null;
        }

        try
        {
            Player.Stop();
            Player.Source = null;
        }
        catch
        {
            // 关闭阶段释放媒体失败无需处理。
        }
    }

    // ---------------- 无边框窗口的拖边调整大小 ----------------

    private const int WM_NCHITTEST = 0x0084;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    /// <summary>左 / 右 / 上边缘多少像素以内算"拖边调整大小"。</summary>
    private const double ResizeBorder = 7;

    /// <summary>底边留给"调整大小"的感应区要更窄 —— 进度条贴底放,别和它抢鼠标。</summary>
    private const double BottomResizeBorder = 0;

    private const int WM_SIZING = 0x0214;

    /// <summary>右键菜单请求。整条拦掉 —— 见 WndProc 里的说明。</summary>
    private const int WM_CONTEXTMENU = 0x007B;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>隐藏窗口,并且完全不碰前台(前台仍归游戏)。</summary>
    private const int SW_HIDE = 0;

    /// <summary>显示窗口但不激活它 —— 游戏收不到 WM_KILLFOCUS,正在按住的键不会被清掉。</summary>
    private const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    /// <summary>
    /// 把前台焦点还给它原来的窗口。
    /// <para>
    /// 打开文件对话框、设置窗口这类操作会抢走前台,关掉之后焦点不会自己回到游戏,
    /// 于是每次操作完还得再点一下游戏才能用键盘。这里在结束之后主动还回去。
    /// 只还"别的进程"的窗口 —— 本来在自己家窗口之间切换就不用还。
    /// </para>
    /// </summary>
    private static void GiveBackFocus(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId)
            return;

        // SetForegroundWindow 有个限制:只有前台进程才有权设置前台窗口。
        // 我们此刻八成已经不是前台了,所以先把自己和当前前台线程"接"在一起借个权限。
        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        uint curThread = GetCurrentThreadId();

        bool attached = fgThread != 0 && fgThread != curThread
                        && AttachThreadInput(curThread, fgThread, true);
        try
        {
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(curThread, fgThread, false);
        }
    }

    /// <summary>把输入法与这个窗口解绑(见 OnSourceInitialized 的说明)。</summary>
    [DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

    // WM_SIZING 的 wParam:正在拖哪条边 / 哪个角
    private const int WMSZ_LEFT = 1;
    private const int WMSZ_RIGHT = 2;
    private const int WMSZ_TOP = 3;
    private const int WMSZ_TOPLEFT = 4;
    private const int WMSZ_TOPRIGHT = 5;
    private const int WMSZ_BOTTOM = 6;
    private const int WMSZ_BOTTOMLEFT = 7;
    private const int WMSZ_BOTTOMRIGHT = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;

        HwndSource? source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);

        // 这个窗口不接受文字输入,把输入法(IME)从它身上摘掉。
        // 否则像讯飞这类输入法会把自己的悬浮工具条挂到画面上,
        // 表现就是"右键一下冒出个 Clear"。解绑后它们就不会再来了。
        ImmAssociateContext(hwnd, IntPtr.Zero);
    }

    /// <summary>
    /// 这个窗口不允许最大化。它是无边框的:一旦最大化就会盖住整屏、又找不到关闭入口
    /// (Win+Up、把窗口拖到屏幕顶端都会触发),所以只要被最大化就立刻还原。
    /// </summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;
    }

    /// <summary>
    /// 无边框 + 分层透明的窗口没有系统非客户区,鼠标压到边上必须自己回 HT*,
    /// 否则整块都被当成客户区,系统永远不会进入调整大小的循环(就是"拖不动边"的原因)。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 等比例拖动:系统算出来的矩形先按视频宽高比修正再交回去。
        if (msg == WM_SIZING && _settings.ProportionalResize)
        {
            var rc = Marshal.PtrToStructure<NativeRect>(lParam);
            ApplyAspectLock(ref rc, wParam.ToInt32());
            Marshal.StructureToPtr(rc, lParam, false);
            return IntPtr.Zero;
        }

        // 右键菜单一律不弹:以前在画面上右键会冒出一个带 clear 的菜单,
        // 点完整个视频窗口就没了(控制条还在)。那个菜单不是 WPF 层弹的,
        // 所以除了 XAML 里的预览拦截,这里在 Win32 消息层再封一道。
        if (msg == WM_CONTEXTMENU)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != WM_NCHITTEST)
            return IntPtr.Zero;

        // lParam:低 16 位是屏幕 X,高 16 位是屏幕 Y
        int screenX = unchecked((short)(long)lParam);
        int screenY = unchecked((short)((long)lParam >> 16));

        Point local = PointFromScreen(new Point(screenX, screenY));

        bool left = local.X <= ResizeBorder;
        bool right = local.X >= ActualWidth - ResizeBorder;
        bool top = local.Y <= ResizeBorder;
        bool bottom = local.Y >= ActualHeight - BottomResizeBorder;

        int hit;

        if (top && left)
            hit = HTTOPLEFT;
        else if (top && right)
            hit = HTTOPRIGHT;
        else if (bottom && left)
            hit = HTBOTTOMLEFT;
        else if (bottom && right)
            hit = HTBOTTOMRIGHT;
        else if (left)
            hit = HTLEFT;
        else if (right)
            hit = HTRIGHT;
        else if (top)
            hit = HTTOP;
        else if (bottom)
            hit = HTBOTTOM;
        else
            return IntPtr.Zero;

        handled = true;
        return new IntPtr(hit);
    }

    /// <summary>
    /// "等比例拖动":系统给的是自由拖拽的矩形,这里按视频宽高比把它修正回去,
    /// 让窗口始终和画面同比例(画面铺满、不留黑边)。拖动的那条边保持不动。
    /// </summary>
    private void ApplyAspectLock(ref NativeRect rc, int edge)
    {
        double aspect = GetAspectRatio();

        if (aspect <= 0.01)
            return;

        int width = rc.Right - rc.Left;
        int height = rc.Bottom - rc.Top;

        // 拖左右边以宽度为准算高度;拖上下边以高度为准算宽度。
        if (edge is WMSZ_LEFT or WMSZ_RIGHT)
            height = (int)Math.Round(width / aspect);
        else
            width = (int)Math.Round(height * aspect);

        if (width < 1 || height < 1)
            return;

        switch (edge)
        {
            case WMSZ_LEFT:
                rc.Left = rc.Right - width;
                rc.Bottom = rc.Top + height;
                break;
            case WMSZ_RIGHT:
                rc.Right = rc.Left + width;
                rc.Bottom = rc.Top + height;
                break;
            case WMSZ_TOP:
                rc.Top = rc.Bottom - height;
                rc.Right = rc.Left + width;
                break;
            case WMSZ_BOTTOM:
                rc.Bottom = rc.Top + height;
                rc.Right = rc.Left + width;
                break;
            case WMSZ_TOPLEFT:
                rc.Left = rc.Right - width;
                rc.Top = rc.Bottom - height;
                break;
            case WMSZ_TOPRIGHT:
                rc.Right = rc.Left + width;
                rc.Top = rc.Bottom - height;
                break;
            case WMSZ_BOTTOMLEFT:
                rc.Left = rc.Right - width;
                rc.Bottom = rc.Top + height;
                break;
            case WMSZ_BOTTOMRIGHT:
                rc.Right = rc.Left + width;
                rc.Bottom = rc.Top + height;
                break;
        }
    }

    /// <summary>当前视频的宽高比;还没打开视频时退回窗口自身比例。</summary>
    private double GetAspectRatio()
    {
        if (Player is not null && Player.NaturalVideoWidth > 0 && Player.NaturalVideoHeight > 0)
            return (double)Player.NaturalVideoWidth / Player.NaturalVideoHeight;

        return ActualWidth > 1 && ActualHeight > 1 ? ActualWidth / ActualHeight : 16.0 / 9.0;
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    // ---------------- 全局热键 ----------------

    /// <summary>
    /// 按当前手势整体重新注册。改键之后调用一次即可 —— 与其维护"哪几个键变了",
    /// 不如整体换一遍,代码简单且不会出现旧热键残留。
    /// </summary>
    private void ApplyHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = new HotkeyManager(this);

        // 先自己查一遍有没有两个动作抢同一个键:同一组合只能注册一次,
        // 后注册的必然失败。把原因写成"与「快进」重复"比笼统的"被其他程序占用"有用得多。
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (HotkeyAction action in _hotkeyActions)
        {
            action.Status = string.Empty;

            if (string.IsNullOrWhiteSpace(action.Gesture))
                continue;

            if (claimed.TryGetValue(action.Gesture, out string? owner))
            {
                action.Status = $"与「{owner}」重复";
                continue;
            }

            claimed[action.Gesture] = action.Name;

            try
            {
                if (!_hotkeys.Register(action.Gesture, action.Callback))
                    action.Status = "被其他程序占用";
            }
            catch (ArgumentException)
            {
                action.Status = "快捷键无效";
            }
        }

        if (_hotkeys.FailedHotkeys.Count > 0)
        {
            Growl.Warning(new GrowlInfo
            {
                Message = "这些全局热键被其他程序占用了: " + string.Join("、", _hotkeys.FailedHotkeys),
                WaitTime = 5
            });
        }
    }

    private void RestoreHotkeyGestures()
    {
        foreach (HotkeyAction action in _hotkeyActions)
        {
            if (_settings.Hotkeys.TryGetValue(action.Key, out string? saved) && !string.IsNullOrWhiteSpace(saved))
                action.Gesture = saved;
        }
    }

    private void SyncHotkeysToSettings()
        => _settings.Hotkeys = _hotkeyActions.ToDictionary(a => a.Key, a => a.Gesture);

    // ---------------- 设置读写 ----------------

    private void ApplySettings()
    {
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);

        if (_settings.WindowLeft is double left
            && _settings.WindowTop is double top
            && IsOnScreen(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            // 上次的位置已不在可见屏幕内(比如换过显示器),回退居中。
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // 这个窗口一直置顶 —— 边玩游戏边看攻略,不置顶就没意义了。
        Topmost = true;
    }

    private void SaveSettings()
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }

        // 存的仍然是"画面透明度"(字段名沿用 WindowOpacity,旧配置照旧能读)。
        // 注意:这里不能拿 VideoArea.Opacity 当用户设置 —— 暂停压暗时它是 30%,
        // 写回去就等于把用户的透明度永久改掉了。用户改动由 SetOpacity 负责记录。
        _settings.Volume = _volumePercent / 100.0;
        _settings.SpeedRatio = GetSpeed();

        SyncHotkeysToSettings();
        _settings.Save();
    }

    private static bool IsOnScreen(double left, double top)
    {
        const double Margin = 60;

        return left > SystemParameters.VirtualScreenLeft - Margin
               && top > SystemParameters.VirtualScreenTop - Margin
               && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Margin
               && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Margin;
    }

    // ---------------- 显示同步 ----------------

    private void ApplyVolume(double percent)
    {
        if (_syncing)
            return;

        _syncing = true;

        try
        {
            double value = Math.Clamp(percent, 0, 100);
            _volumePercent = value;
            _muted = value <= 0.01;

            if (Player is not null)
                Player.Volume = value / 100.0;
        }
        finally
        {
            _syncing = false;
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// 透明度只作用在画面区(<see cref="VideoArea"/>)上,不是整个窗口。
    /// 进度条就在画面区里面,所以画面、进度条、时间是一起淡下去的。
    /// </summary>
    private void ApplyOpacity(double percent)
    {
        if (_syncing)
            return;

        _syncing = true;

        try
        {
            double ratio = Math.Clamp(percent, 30, 100) / 100.0;

            if (VideoArea is not null)
                VideoArea.Opacity = ratio;
        }
        finally
        {
            _syncing = false;
        }

        RaiseStateChanged();
    }

    private void ApplySpeed(int index)
    {
        if (_syncing)
            return;

        _syncing = true;

        try
        {
            int clamped = Math.Clamp(index, 0, PlaybackSpeeds.Length - 1);
            _speedRatio = PlaybackSpeeds[clamped];

            // 用户设定的倍速;按住快进时的临时 2 倍速在松开后会恢复成它。
            if (Player is not null && !_holdTimer.IsEnabled)
                Player.SpeedRatio = _speedRatio;
        }
        finally
        {
            _syncing = false;
        }

        RaiseStateChanged();
    }

    private void ChangeOpacity(double delta)
    {
        ApplyOpacity(VideoArea.Opacity * 100.0 + delta);

        // 固定 token:连按快捷键时只刷新同一条提示,不会叠一屏。
        Growl.Info($"画面透明度 {VideoArea.Opacity * 100:0}%", "opacity");
    }

    // ---------------- 打开与播放 ----------------

    /// <summary>打开文件对话框。控制条上的"打开"按钮也走这里,故为 internal。</summary>
    internal void OpenFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择视频",
            Multiselect = true,
            Filter = "视频文件|*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm;*.flv;*.ts;*.mpg;*.mpeg;*.3gp|所有文件|*.*"
        };

        if (!string.IsNullOrWhiteSpace(_settings.LastDirectory) && Directory.Exists(_settings.LastDirectory))
            dialog.InitialDirectory = _settings.LastDirectory;

        IntPtr previous = GetForegroundWindow();

        if (dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0)
            StartPlaylist(dialog.FileNames, 0);

        // 对话框关掉后把前台还给游戏,省得每次选完片还得再点一下游戏。
        GiveBackFocus(previous);
    }

    private void StartPlaylist(IReadOnlyList<string> files, int startIndex)
    {
        _playlist.Clear();
        _playlist.AddRange(files);
        _index = -1;

        if (_playlist.Count == 0)
            return;

        _settings.LastDirectory = Path.GetDirectoryName(_playlist[0]);
        PlayAt(startIndex);
    }

    private void PlayAt(int index)
    {
        if (_playlist.Count == 0)
            return;

        // 支持首尾环绕,这样"上一集/下一集"永远有响应。
        _index = ((index % _playlist.Count) + _playlist.Count) % _playlist.Count;

        string path = _playlist[_index];

        try
        {
            Player.Stop();
            Player.Source = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            Player.Play();

            _isPlaying = true;
            _timer.Start();
        }
        catch (Exception ex)
        {
            Growl.Error(new GrowlInfo
            {
                Message = $"无法打开 {Path.GetFileName(path)}: {ex.Message}",
                WaitTime = 4
            });
            return;
        }

        UpdateFileName();
        UpdateHint();
        RaiseStateChanged();
    }

    private void Seek(double seconds)
    {
        if (!_ready || Player.Source is null || !Player.NaturalDuration.HasTimeSpan)
            return;

        TimeSpan total = Player.NaturalDuration.TimeSpan;
        TimeSpan target = Player.Position + TimeSpan.FromSeconds(seconds);

        if (target < TimeSpan.Zero)
            target = TimeSpan.Zero;
        if (target > total)
            target = total;

        Player.Position = target;
        RefreshProgress();
    }

    // ---------------- 媒体事件 ----------------

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        // MediaElement 的 Volume / SpeedRatio 在媒体真正打开后设置才可靠。
        Player.Volume = _muted ? 0 : _volumePercent / 100.0;
        Player.SpeedRatio = GetSpeed();
        _timer.Start();
        RaiseStateChanged();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_settings.Loop)
        {
            Player.Position = TimeSpan.Zero;
            Player.Play();
            return;
        }

        // 播放列表里还有下一集就自动续播,否则停在末尾。
        if (_index >= 0 && _index < _playlist.Count - 1)
        {
            PlayNext();
            return;
        }

        _isPlaying = false;
        EndSeekHold();
        _timer.Stop();
        RaiseStateChanged();
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _isPlaying = false;
        RaiseStateChanged();

        Growl.Error(new GrowlInfo
        {
            Message = $"这个视频播放失败(系统可能缺少对应解码器): {e.ErrorException?.Message}",
            WaitTime = 5
        });
    }

    // ---------------- 进度 ----------------

    private void RefreshProgress()
    {
        if (!_ready || _isScrubbing)
            return;

        if (!Player.NaturalDuration.HasTimeSpan)
        {
            TimeText.Text = "--:-- / --:--";
            return;
        }

        TimeSpan total = Player.NaturalDuration.TimeSpan;
        TimeSpan position = Player.Position;

        _suppressProgressEvent = true;
        ProgressSlider.Value = total.TotalSeconds > 0.01
            ? position.TotalSeconds / total.TotalSeconds * 100.0
            : 0;
        _suppressProgressEvent = false;


        TimeText.Text = $"{FormatTime(position)} / {FormatTime(total)}";
    }

    /// <summary>
    /// 拖动开始:停掉刷新定时器,并进入"只预览、不 Seek"的状态。
    /// 之前每动一下就往 Player.Position 里写一次(相当于每秒几十次 seek),
    /// 每次 seek 都要在 UI 线程上让解码器重新定位,所以拖起来会卡死。
    /// </summary>
    private void Progress_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isScrubbing = true;
        _timer.Stop();
    }

    private void Progress_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isScrubbing = false;
        SeekToPercent(ProgressSlider.Value);

        // 再按实际播放位置回刷一次圆点:如果这次 seek 没能立刻生效,
        // 圆点也不会停在"手指松开的地方"和画面对不上。
        RefreshProgress();

        if (_isPlaying)
            _timer.Start();
    }

    private void Progress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _suppressProgressEvent || !Player.NaturalDuration.HasTimeSpan)
            return;

        TimeSpan total = Player.NaturalDuration.TimeSpan;

        if (_isScrubbing)
        {
            // 拖动过程中只更新时间文字,松手后才真正 seek 一次。
            TimeSpan preview = TimeSpan.FromSeconds(total.TotalSeconds * e.NewValue / 100.0);
            TimeText.Text = $"{FormatTime(preview)} / {FormatTime(total)}";
            return;
        }

        // 直接点在进度条空白处(没有拖动)也当作一次跳转。
        SeekToPercent(e.NewValue);
    }

    private void SeekToPercent(double percent)
    {
        if (!Player.NaturalDuration.HasTimeSpan)
            return;

        TimeSpan total = Player.NaturalDuration.TimeSpan;
        Player.Position = TimeSpan.FromSeconds(total.TotalSeconds * Math.Clamp(percent, 0, 100) / 100.0);
        TimeText.Text = $"{FormatTime(Player.Position)} / {FormatTime(total)}";
    }

    // ---------------- 画面上的鼠标操作 ----------------

    private void Video_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 单击不再切换播放/暂停,双击也不做任何事(全屏功能已移除),这里只负责拖动窗口。
        _dragOrigin = e.GetPosition(this);
        _dragCandidate = true;
    }

    private void Video_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || e.LeftButton != MouseButtonState.Pressed)
            return;

        Point current = e.GetPosition(this);

        if (Math.Abs(current.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // 拖出阈值就当成"移动窗口"(无边框窗口没有标题栏可拖)。
        _dragCandidate = false;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 窗口状态变化时 DragMove 会抛异常,忽略。
        }
    }

    private void Video_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ApplyVolume(_volumePercent + (e.Delta > 0 ? 5 : -5));
        e.Handled = true;
    }

    /// <summary>
    /// 主窗口上不响应右键。
    /// <para>
    /// 那个冒出 "Clear" 的东西来自 HandyControl 的 Growl 宿主(根 Grid 上的 GrowlParent)
    /// 自带的上下文菜单 —— 它显示在宿主中央,看着就像浮在画面中间。
    /// 只拦 Down 没用:WPF 的 ContextMenu 是 <b>MouseRightButtonUp</b> 触发的。
    /// XAML 里还顺手关掉了整个窗口的 ContextMenuService,双保险。
    /// </para>
    /// </summary>
    private void Window_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        => e.Handled = true;

    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        // 窗口内的左右键也要像全局热键那样"按住加速、松开恢复"。
        if (e.Key is Key.Left or Key.Right)
        {
            _heldByButton = false;
            EndSeekHold();
        }
    }

    // ---------------- 拖放 ----------------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0)
            return;

        var videos = new List<string>();

        foreach (string path in dropped)
        {
            if (Directory.Exists(path))
            {
                // 拖入文件夹时,连同子目录一起收集视频并按路径排序,顺序更符合直觉。
                videos.AddRange(Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                    .Where(IsVideoFile)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            }
            else if (File.Exists(path) && IsVideoFile(path))
            {
                videos.Add(path);
            }
        }

        if (videos.Count == 0)
        {
            Growl.Warning(new GrowlInfo { Message = "没找到可播放的视频文件。", WaitTime = 3 });
            return;
        }

        StartPlaylist(videos, 0);
    }

    private static bool IsVideoFile(string path)
        => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // ---------------- 显示状态 ----------------

    private void UpdateFileName()
    {
        if (_index < 0 || _index >= _playlist.Count)
            return;

        string name = Path.GetFileName(_playlist[_index]);

        // 文件名只体现在窗口标题和顶部控制条上(窗口里没有任何文字)。
        _fileLabel = _playlist.Count > 1
            ? $"{name}   ({_index + 1}/{_playlist.Count})"
            : name;

        Title = $"{Path.GetFileNameWithoutExtension(name)} - CheatSheet";
    }

    private void UpdateHint()
        => HintText.Visibility = Player.Source is null ? Visibility.Visible : Visibility.Collapsed;

    private int GetSpeedIndex()
        => Math.Clamp(SpeedToIndex(GetSpeed()), 0, PlaybackSpeeds.Length - 1);

    private double GetSpeed() => _speedRatio;

    private static int SpeedToIndex(double speed)
    {
        int index = Array.IndexOf(PlaybackSpeeds, speed);
        return index >= 0 ? index : 2;
    }

    private static string FormatTime(TimeSpan value)
        => value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"mm\:ss");
}
