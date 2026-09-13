using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CheatSheet;

/// <summary>
/// 贴在屏幕正上方的独立控制条。
/// <para>
/// 它不是播放窗口的一部分:播放窗口只有画面,所有操作都集中在这条上。
/// 两处细节是为"边玩游戏边看攻略"服务的:窗口带 <c>WS_EX_NOACTIVATE</c>,
/// 点它不会抢焦点(游戏不会被切出全屏);带 <c>WS_EX_TOOLWINDOW</c>,不进 Alt+Tab 和任务栏。
/// </para>
/// </summary>
public partial class ControlBarWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out MonitorRect lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public MonitorRect rcMonitor;
        public MonitorRect rcWork;
        public uint dwFlags;
    }

    /// <summary>(物理像素)鼠标移到屏幕顶部这么多像素以内就把条放出来。</summary>
    private const int ShowZone = 8;

    private readonly MainWindow _main;
    private readonly DispatcherTimer _autoHide;
    private bool _syncing;
    private bool _visible = true;

    /// <summary>正在地址栏里打字。期间要保持条不收起,而且焦点不能交还给游戏。</summary>
    private bool _editing;

    /// <summary>开始编辑前的前台窗口(通常是游戏),打完字还给它。</summary>
    private IntPtr _focusBeforeEdit;

    /// <summary>出现 / 收起时的竖向位移(淡入淡出的同时在动这一项)。</summary>
    private readonly TranslateTransform _slide = new();

    private static readonly Duration FadeIn = TimeSpan.FromMilliseconds(150);
    private static readonly Duration FadeOut = TimeSpan.FromMilliseconds(120);
    private static readonly Duration SlideIn = TimeSpan.FromMilliseconds(180);

    public ControlBarWindow(MainWindow main)
    {
        ArgumentNullException.ThrowIfNull(main);

        _main = main;
        InitializeComponent();

        RenderTransform = _slide;

        _main.StateChanged += OnMainStateChanged;

        Loaded += (_, _) =>
        {
            Sync();
            Reposition();
        };

        // 条上文字(文件名)变化会改变自身宽度,宽度一变就重新居中。
        SizeChanged += (_, _) => Reposition();

        // 平时收起来,鼠标移到屏幕顶部才出来。窗口带 WS_EX_NOACTIVATE,
        // 收正常的鼠标进出事件不可靠,所以直接轮询光标位置(很便宜)。
        _autoHide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _autoHide.Tick += (_, _) => UpdateAutoHide();
        _autoHide.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>把条摆到主窗口所在显示器的正上方、水平居中。</summary>
    public void Reposition()
    {
        if (!IsLoaded)
            return;

        IntPtr monitor = MonitorFromWindow(new WindowInteropHelper(_main).Handle, MONITOR_DEFAULTTONEAREST);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };

        if (!GetMonitorInfo(monitor, ref info))
            return;

        UpdateLayout();

        // GetMonitorInfo 给的是物理像素,而 WPF 的 Left/Top 是设备无关单位(DIP)。
        // 高 DPI 缩放下两者混用会把条推到偏右(实测 125% 缩放时偏了约 190px),必须换算。
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);

        double screenWidth = info.rcMonitor.Right - info.rcMonitor.Left;
        double barWidth = ActualWidth * dpi.DpiScaleX;

        Left = Math.Round((info.rcMonitor.Left + ((screenWidth - barWidth) / 2)) / dpi.DpiScaleX);
        Top = Math.Round(info.rcMonitor.Top / dpi.DpiScaleY);
    }

    /// <summary>鼠标在屏幕顶部一小条内、或已经停在条上 → 显示;否则收起。</summary>
    private void UpdateAutoHide()
    {
        // 正在地址栏里打字:不管鼠标跑哪去了都别收起,不然输入框会跟着窗口一起消失。
        if (_editing)
        {
            SetVisible(true);
            return;
        }

        if (!GetCursorPos(out POINT cursor))
            return;

        // 用 GetWindowRect 取窗口的物理矩形,与 GetCursorPos 同一套坐标系 ——
        // 不再做 WPF 的 DIP 换算(之前换算没对上,导致条右半边被判成"不在条上")。
        bool hasRect = GetWindowRect(new WindowInteropHelper(this).Handle, out MonitorRect rect);

        bool nearTop = cursor.Y <= ShowZone;
        bool onBar = hasRect
                     && cursor.X >= rect.Left && cursor.X <= rect.Right
                     && cursor.Y >= rect.Top && cursor.Y <= rect.Bottom;

        SetVisible(nearTop || onBar);
    }

    private void SetVisible(bool visible)
    {
        if (_visible == visible)
            return;

        _visible = visible;

        // 窗口一直存在,只是不显示 —— 靠轮询光标随时把它叫回来。
        // 出现 / 收起都带过渡:淡入淡出 + 一点点纵向滑动,别生硬地闪出来。
        if (visible)
        {
            Visibility = Visibility.Visible;

            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeIn)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10, 0, SlideIn)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else
        {
            var fade = new DoubleAnimation(1, 0, FadeOut)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            // 淡出播完才真正隐藏,否则动画根本看不到。
            fade.Completed += (_, _) =>
            {
                // 把动画停掉、值落到属性上,免得下次动画的起点不对。
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
                Visibility = Visibility.Hidden;
            };

            BeginAnimation(OpacityProperty, fade);
        }
    }

    private void OnMainStateChanged(object? sender, EventArgs e) => Sync();

    /// <summary>把主窗口的当前状态(播放 / 静音 / 置顶 / 倍速 / 音量 / 文件名 / 地址)拉到条上。</summary>
    private void Sync()
    {
        _syncing = true;

        try
        {
            PlayButton.Content = _main.IsPlaying ? "\uE769" : "\uE768";
            MuteButton.Content = _main.IsMuted ? "\uE74F" : "\uE767";
            SpeedButton.Content = _main.SpeedLabel;
            VolumeSlider.Value = _main.VolumePercent;
            TitleText.Text = _main.FileLabel;

            // 地址栏只在浏览器模式下出现。
            AddressRow.Visibility = _main.IsWebMode ? Visibility.Visible : Visibility.Collapsed;

            // 切回本地视频模式时,地址栏连同编辑状态一起收掉(否则窗口会一直占着前台)。
            if (!_main.IsWebMode && _editing)
                EndEditing();

            // 正在打字时别覆盖用户输了一半的内容;光标也不该乱跳。
            string url = _main.WebCurrentUrl;
            if (!_editing && !string.IsNullOrWhiteSpace(url) && AddressBox.Text != url)
                AddressBox.Text = url;
        }
        finally
        {
            _syncing = false;
        }
    }

    // ---------------- 地址栏 ----------------

    /// <summary>
    /// 控制条是用 <c>WS_EX_NOACTIVATE</c> 挂在屏幕顶上的,代价是它永远不是"活动窗口",
    /// 键盘输入进不来 —— 直接在地址栏里打字,字会全打到游戏里去。所以进编辑状态时
    /// 临时把这个样式摘掉、把前台抢过来,打完字(回车 / Esc / 焦点跑掉)再装回去。
    /// </summary>
    private void BeginEditing()
    {
        if (_editing || AddressRow.Visibility != Visibility.Visible)
            return;

        _editing = true;
        _focusBeforeEdit = GetForegroundWindow();

        IntPtr handle = new WindowInteropHelper(this).Handle;

        SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) & ~WS_EX_NOACTIVATE);
        MainWindow.ForceForeground(handle);
        Activate();

        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    /// <summary>退出编辑状态,把样式装回去、前台还给游戏。</summary>
    private void EndEditing()
    {
        if (!_editing)
            return;

        _editing = false;

        IntPtr handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE);

        Keyboard.ClearFocus();

        // 只在前台"还在我们这条控制条上"时才交还 —— 用户如果已经点了别的窗口
        // (比如去点画面),那他要的就是那个窗口,这时候把前台抢回给游戏是帮倒忙。
        if (GetForegroundWindow() == handle)
            MainWindow.GiveBackFocus(_focusBeforeEdit);
    }

    private void AddressRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginEditing();

    private void Address_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => BeginEditing();

    private void Address_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 焦点跑掉了(比如 Alt+Tab 出去):别再占着前台不放。
        if (_editing && !AddressBox.IsKeyboardFocusWithin)
            EndEditing();
    }

    private void Address_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateToAddress();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // 放弃这次输入,把框里改回当前真实地址。
            AddressBox.Text = _main.WebCurrentUrl;
            EndEditing();
            e.Handled = true;
        }
    }

    private void Go_Click(object sender, RoutedEventArgs e) => NavigateToAddress();

    private void NavigateToAddress()
    {
        _main.OpenWebUrl(AddressBox.Text);
        EndEditing();
    }

    private void Open_Click(object sender, RoutedEventArgs e) => _main.OpenFiles();

    private void Previous_Click(object sender, RoutedEventArgs e) => _main.PlayPrevious();

    private void PlayPause_Click(object sender, RoutedEventArgs e) => _main.TogglePlayPause();

    private void Next_Click(object sender, RoutedEventArgs e) => _main.PlayNext();

    // -5s / +5s:按下就开跳并切 2 倍速,松开恢复(单击 = 只跳 5 秒)。
    private void Rewind_Press(object sender, MouseButtonEventArgs e) => _main.BeginSeekHoldByButton(-1);

    private void Rewind_Release(object sender, MouseButtonEventArgs e) => _main.EndSeekHoldByButton();

    private void Forward_Press(object sender, MouseButtonEventArgs e) => _main.BeginSeekHoldByButton(1);

    private void Forward_Release(object sender, MouseButtonEventArgs e) => _main.EndSeekHoldByButton();

    private void Speed_Click(object sender, RoutedEventArgs e) => _main.CycleSpeed();

    private void Mute_Click(object sender, RoutedEventArgs e) => _main.ToggleMute();

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing)
            return;

        _main.SetVolume(e.NewValue);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _main.OpenSettings();

    private void Exit_Click(object sender, RoutedEventArgs e) => _main.Close();
}
