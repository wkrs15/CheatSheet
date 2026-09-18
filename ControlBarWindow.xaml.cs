using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    /// <summary>
    /// 选集 / 最近 下拉栏正开着。和地址栏编辑一样:这期间不能把条收起来 ——
    /// 鼠标一旦从屏幕顶端移下来(去点列表),轮询就会判成"该收起",条一藏弹层也跟着没了。
    /// <para>
    /// 直接看两个弹层的实际状态,不用自己维护标志位:两个弹层互相切换时
    /// (StaysOpen=False 会先关掉上一个)"先开后关"的顺序会把标志位清错。
    /// </para>
    /// </summary>
    private bool MenuOpen => ChapterPopup.IsOpen || RecentPopup.IsOpen || PlayerPopup.IsOpen;

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
        // 正在地址栏里打字 / 正开着下拉栏:不管鼠标跑哪去了都别收起,
        // 不然输入框、弹出来的列表会跟着窗口一起消失。
        if (_editing || MenuOpen)
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
            // 条要收起来了,弹在外面的下拉栏不能留着(它会悬在屏幕中间不动)。
            if (ChapterPopup.IsOpen)
                ChapterPopup.IsOpen = false;

            if (RecentPopup.IsOpen)
                RecentPopup.IsOpen = false;

            if (PlayerPopup.IsOpen)
                PlayerPopup.IsOpen = false;

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

            SyncChapters();
            SyncRecent();
            SyncPlayerOptions();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>把「最近观看」下拉栏刷成主窗口那边的内容(空的时候整条收起来)。</summary>
    private void SyncRecent()
    {
        IReadOnlyList<MainWindow.RecentItem> items = _main.RecentItems;

        if (items.Count == 0)
        {
            if (RecentPopup.IsOpen)
                RecentPopup.IsOpen = false;

            RecentToggle.Visibility = Visibility.Collapsed;
            RecentList.ItemsSource = null;
            return;
        }

        RecentToggle.Visibility = Visibility.Visible;

        // 列表是主窗口缓存着的:内容没变就别换 ItemsSource(否则滚动位置会被弹回去)。
        if (!ReferenceEquals(RecentList.ItemsSource, items))
            RecentList.ItemsSource = items;

        // 胶囊上写"最近"而不是最新那条的名字:名字和时间轴标题长得太像,
        // 用户根本认不出这是个下拉栏(2026-09-18 的实际反馈)。具体看 ToolTip。
        RecentToggle.Content = items.Count > 1 ? $"最近 {items.Count}" : "最近";
        RecentToggle.ToolTip = $"最近看过的 {items.Count} 条(本地文件 + 网页)\n最新:{items[0].Name}";
    }

    /// <summary>
    /// 「播放器」下拉栏:网页模式下把播放器自己的画质 / 弹幕暴露出来。
    /// 没读到画质菜单(本地模式、或者播放器还没渲染出来)就整条收起来。
    /// </summary>
    private void SyncPlayerOptions()
    {
        if (!_main.HasWebPlayerOptions)
        {
            if (PlayerPopup.IsOpen)
                PlayerPopup.IsOpen = false;

            PlayerToggle.Visibility = Visibility.Collapsed;
            QualityList.ItemsSource = null;
            return;
        }

        PlayerToggle.Visibility = Visibility.Visible;

        IReadOnlyList<MainWindow.ChapterItem> qualities = _main.WebQualityItems;

        if (!ReferenceEquals(QualityList.ItemsSource, qualities))
            QualityList.ItemsSource = qualities;

        string quality = _main.WebQualityLabel;
        PlayerToggle.Content = quality.Length > 0 ? quality : "播放器";

        DanmakuStateText.Text = _main.WebDanmakuOn switch
        {
            true => "开",
            false => "关",
            _ => "–"
        };
    }

    // ---------------- 播放器下拉栏(画质 / 弹幕) ----------------

    private void PlayerToggle_Click(object sender, RoutedEventArgs e)
        => PlayerPopup.IsOpen = PlayerToggle.IsChecked == true;

    private void PlayerPopup_Opened(object sender, EventArgs e)
    {
        KeepBarAliveForMenu();

        if (PresentationSource.FromVisual(PlayerPopup.Child) is HwndSource source)
        {
            IntPtr handle = source.Handle;
            SetWindowLong(handle, GWL_EXSTYLE,
                GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }
    }

    private void PlayerPopup_Closed(object sender, EventArgs e)
    {
        PlayerToggle.IsChecked = false;
    }

    private void QualityItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MainWindow.ChapterItem item })
            return;

        PlayerPopup.IsOpen = false;
        _main.SelectWebQuality(item.Index);
    }

    private void Danmaku_Click(object sender, RoutedEventArgs e)
        => _main.ToggleWebDanmaku();

    /// <summary>
    /// 把选集下拉栏刷成主窗口那边的当前内容(网页模式 = B 站分P,本地 = 播放列表)。
    /// 只有一项时藏起来 —— 没得选的下拉栏只是占地方。
    /// </summary>
    private void SyncChapters()
    {
        IReadOnlyList<MainWindow.ChapterItem> chapters = _main.Chapters;

        if (chapters.Count <= 1)
        {
            if (ChapterPopup.IsOpen)
                ChapterPopup.IsOpen = false;

            ChapterToggle.Visibility = Visibility.Collapsed;
            ChapterList.ItemsSource = null;
            return;
        }

        ChapterToggle.Visibility = Visibility.Visible;

        // 列表本身是主窗口缓存着的:只在"内容真的变了"时才换一次 ItemsSource,
        // 否则每次状态变化都重建,用户正在滚列表就会被弹回顶部。
        if (!ReferenceEquals(ChapterList.ItemsSource, chapters))
            ChapterList.ItemsSource = chapters;

        int current = _main.ChapterIndex;
        ChapterToggle.Content = current >= 0 && current < chapters.Count
            ? chapters[current].Label
            : $"{chapters.Count} 项";
    }

    // ---------------- 选集下拉栏 ----------------

    private void ChapterToggle_Click(object sender, RoutedEventArgs e)
        => ChapterPopup.IsOpen = ChapterToggle.IsChecked == true;

    /// <summary>
    /// 弹层是<b>独立的顶层窗口</b>,控制条身上那套 <c>WS_EX_NOACTIVATE</c> 不会继承过去 ——
    /// 不补一刀的话,弹出 / 点击都会把前台从游戏那儿抢走(游戏会被踢出全屏)。
    /// 所以这里给弹层自己也装上同样的样式。
    /// </summary>
    private void ChapterPopup_Opened(object sender, EventArgs e)
    {
        KeepBarAliveForMenu();

        if (PresentationSource.FromVisual(ChapterPopup.Child) is HwndSource source)
        {
            IntPtr handle = source.Handle;
            SetWindowLong(handle, GWL_EXSTYLE,
                GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }
    }

    /// <summary>
    /// 弹层一开就先把条亮出来(并挡住自动收起):弹层紧贴条的下沿,
    /// 而"鼠标已经不在屏幕顶端的 8px 里了"会让轮询立刻开始收条。
    /// </summary>
    private void KeepBarAliveForMenu() => SetVisible(true);

    private void ChapterPopup_Closed(object sender, EventArgs e)
    {
        ChapterToggle.IsChecked = false;
    }

    private void ChapterItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MainWindow.ChapterItem item })
            return;

        ChapterPopup.IsOpen = false;
        _main.SelectChapter(item);
    }

    // ---------------- 最近观看下拉栏 ----------------

    private void RecentToggle_Click(object sender, RoutedEventArgs e)
        => RecentPopup.IsOpen = RecentToggle.IsChecked == true;

    private void RecentPopup_Opened(object sender, EventArgs e)
    {
        KeepBarAliveForMenu();

        // 和控制条一样:弹层是独立顶层窗口,不补 NOACTIVATE 就会把前台从游戏那儿抢走。
        if (PresentationSource.FromVisual(RecentPopup.Child) is HwndSource source)
        {
            IntPtr handle = source.Handle;
            SetWindowLong(handle, GWL_EXSTYLE,
                GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }
    }

    private void RecentPopup_Closed(object sender, EventArgs e)
    {
        RecentToggle.IsChecked = false;
    }

    private void RecentItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MainWindow.RecentItem item })
            return;

        RecentPopup.IsOpen = false;
        _main.PlayRecent(item);
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
