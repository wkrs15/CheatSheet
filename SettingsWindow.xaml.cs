using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CheatSheet;

/// <summary>
/// 独立的设置窗口(不是嵌在播放窗口里的面板)。
/// <para>
/// 它只负责界面:所有改动都通过 <see cref="MainWindow"/> 的 internal 命令落地,
/// 播放窗口状态变化时再通过 <see cref="MainWindow.StateChanged"/> 把值同步回来。
/// 热键录制由本窗口接收键盘(所以它必须是可激活的普通窗口,不能像控制条那样 NOACTIVATE)。
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MainWindow _main;
    private HotkeyAction? _recordingAction;
    private bool _syncing;

    /// <summary>检查到的新版本(没有就是 null)。"打开下载页"用它。</summary>
    private UpdateChecker.UpdateInfo? _availableUpdate;

    /// <summary>
    /// XAML 里滑块/下拉的初始值会在 InitializeComponent 期间就触发一次事件,
    /// 那时窗口还没 Loaded。不挡住的话,一打开设置窗口就会把画面透明度和音量刷成默认值。
    /// </summary>
    private bool _initialized;

    public SettingsWindow(MainWindow main)
    {
        ArgumentNullException.ThrowIfNull(main);

        _main = main;
        InitializeComponent();

        // 播放窗口一直置顶,设置窗口跟着置顶,免得游戏全屏时看不到它。
        Topmost = true;

        HotkeyList.ItemsSource = _main.HotkeyActions;
        _main.StateChanged += OnMainStateChanged;

        VersionText.Text = _main.VersionLabel;

        // 下拉框里放"控制条出现延迟"的档位(立即 / 0.2 / 0.4 / 0.8 秒)。
        BarDelayBox.ItemsSource = MainWindow.ControlBarDelayChoices.Select(choice => choice.Label).ToList();

        Loaded += (_, _) =>
        {
            _initialized = true;
            Sync();
        };
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _main.StateChanged -= OnMainStateChanged;

        // 万一在录制中直接把设置窗口关掉,别让全局热键一直停摆。
        if (_recordingAction is not null)
        {
            _recordingAction.Status = string.Empty;
            _recordingAction = null;
            _main.CommitHotkeyChange();
        }
    }

    private void OnMainStateChanged(object? sender, EventArgs e) => Sync();

    /// <summary>把播放窗口的当前值拉到面板上。</summary>
    private void Sync()
    {
        _syncing = true;

        try
        {
            double opacity = _main.VideoOpacityPercent;
            OpacitySlider.Value = opacity;
            OpacityValueText.Text = $"{opacity:0}%";

            double volume = _main.VolumePercent;
            VolumeSlider.Value = volume;
            VolumeValueText.Text = $"{volume:0}%";

            int speedIndex = _main.SpeedIndex;
            if (SpeedBox.SelectedIndex != speedIndex)
                SpeedBox.SelectedIndex = speedIndex;

            ProportionalBox.IsChecked = _main.ProportionalResize;

            ResumeBox.IsChecked = _main.RememberPosition;
            LoopBox.IsChecked = _main.LoopPlayback;
            AutoUpdateBox.IsChecked = _main.CheckUpdatesOnStart;

            // 控制条出现延迟:配置里存的是毫秒,下拉框里选**最接近**的那一档
            // (档位以后可能会调整,不能假设存的值一定在列表里)。
            int delayIndex = 0;
            int bestDistance = int.MaxValue;

            for (int i = 0; i < MainWindow.ControlBarDelayChoices.Length; i++)
            {
                int distance = Math.Abs(MainWindow.ControlBarDelayChoices[i].Milliseconds - _main.ControlBarDelayMs);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    delayIndex = i;
                }
            }

            if (BarDelayBox.SelectedIndex != delayIndex)
                BarDelayBox.SelectedIndex = delayIndex;

            // 播放模式:本地视频 / 浏览器。
            if (_main.IsWebMode)
                WebModeBox.IsChecked = true;
            else
                VideoModeBox.IsChecked = true;

            if (HomeUrlBox.Text != _main.WebHomeUrl)
                HomeUrlBox.Text = _main.WebHomeUrl;

            switch (_main.PauseBehavior)
            {
                case 0:
                    PauseNothingBox.IsChecked = true;
                    break;
                case 1:
                    PauseHideBox.IsChecked = true;
                    break;
                default:
                    PauseDimBox.IsChecked = true;
                    break;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Opacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !_initialized)
            return;

        _main.SetOpacity(e.NewValue);
    }

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !_initialized)
            return;

        _main.SetVolume(e.NewValue);
    }

    private void Speed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !_initialized)
            return;

        _main.SetSpeedIndex(SpeedBox.SelectedIndex);
    }

    private void HotkeyCapture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HotkeyAction action })
            return;

        // 上一次没录完的行先复位,避免两行同时显示"按下组合键"。
        if (_recordingAction is not null && _recordingAction != action)
            _recordingAction.Status = string.Empty;

        // 录制期间先注销全局热键:否则按到已绑定的组合会被热键截走,录不进去。
        _main.SuspendHotkeys();

        _recordingAction = action;
        action.Status = "按下组合键…";
        e.Handled = true;
    }

    private void PauseBehavior_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || !_initialized)
            return;

        _main.PauseBehavior = PauseNothingBox.IsChecked == true ? 0
                            : PauseHideBox.IsChecked == true ? 1
                            : 2;
    }

    private void PlayMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || !_initialized)
            return;

        _main.SetWebMode(WebModeBox.IsChecked == true);
    }

    private void HomeUrl_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing || !_initialized)
            return;

        _main.WebHomeUrl = HomeUrlBox.Text;
    }

    private void Proportional_Click(object sender, RoutedEventArgs e)
        => _main.ProportionalResize = ProportionalBox.IsChecked == true;

    private void Resume_Click(object sender, RoutedEventArgs e)
        => _main.RememberPosition = ResumeBox.IsChecked == true;

    /// <summary>控制条"出现延迟"下拉框(存活 0.2 / 0.4 / 0.8 秒)。</summary>
    private void BarDelay_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !_initialized)
            return;

        int index = BarDelayBox.SelectedIndex;

        if (index >= 0 && index < MainWindow.ControlBarDelayChoices.Length)
            _main.ControlBarDelayMs = MainWindow.ControlBarDelayChoices[index].Milliseconds;
    }

    private void Loop_Click(object sender, RoutedEventArgs e)
        => _main.LoopPlayback = LoopBox.IsChecked == true;

    // ---------------- 关于 / 检查更新 ----------------

    private void AutoUpdate_Click(object sender, RoutedEventArgs e)
        => _main.CheckUpdatesOnStart = AutoUpdateBox.IsChecked == true;

    /// <summary>
    /// 手动检查更新。
    /// <para>
    /// 事件处理器本身是同步的,真正的检查丢给 <see cref="CheckForUpdatesCoreAsync"/> ——
    /// 这样不用写 <c>async void</c>,异常也不会跑到没人接的地方去。
    /// </para>
    /// </summary>
    private void CheckUpdate_Click(object sender, RoutedEventArgs e) => _ = CheckForUpdatesCoreAsync();

    private async Task CheckForUpdatesCoreAsync()
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查…";

        try
        {
            MainWindow.UpdateCheckResult result = await MainWindow.CheckForUpdatesAsync();

            if (!result.Succeeded)
            {
                // "查不到"和"已经是最新"必须分开说,不然用户会以为自己是最新版。
                UpdateStatusText.Text = "检查失败:网络不通,或者访问 GitHub 受限";
                _availableUpdate = null;
            }
            else if (result.Update is null)
            {
                UpdateStatusText.Text = $"已经是最新版本({_main.VersionLabel})";
                _availableUpdate = null;
            }
            else
            {
                _availableUpdate = result.Update;
                UpdateStatusText.Text = $"发现新版本 {result.Update.Tag}(当前 {_main.VersionLabel})";
            }

            DownloadButton.IsEnabled = _availableUpdate is not null;
            InstallUpdateButton.IsEnabled = _availableUpdate is not null;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OpenDownloadPage_Click(object sender, RoutedEventArgs e)
        => MainWindow.OpenInBrowser(_availableUpdate?.PageUrl ?? UpdateChecker.ReleasesPage);

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e) => MainWindow.OpenConfigFolder();

    // ---------------- 自动更新(下载 → 替换 → 重启) ----------------

    private void InstallUpdate_Click(object sender, RoutedEventArgs e) => _ = InstallUpdateCoreAsync();

    /// <summary>
    /// 下载新版本并交给替换器。流程:先把东西全准备好(下载 + 解压 + 校验),
    /// 确认无误后才问用户"现在换吗" —— 替换会让程序重启,不该让人白等一场。
    /// </summary>
    private async Task InstallUpdateCoreAsync()
    {
        if (_availableUpdate is null)
            return;

        InstallUpdateButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;

        try
        {
            if (!UpdateInstaller.CanWriteInstallDirectory())
            {
                UpdateStatusText.Text = "程序所在目录没有写权限(比如装在 Program Files),请点「打开下载页」手动更新";
                return;
            }

            var progress = new Progress<double>(value => UpdateStatusText.Text = $"正在下载… {value:0}%");

            string? zipPath = await UpdateInstaller.DownloadAsync(_availableUpdate, progress);

            if (zipPath is null)
            {
                UpdateStatusText.Text = "下载失败:网络不通或被限流 —— 可以点「打开下载页」手动下载";
                return;
            }

            UpdateStatusText.Text = "正在解压…";

            string? sourceFolder = await Task.Run(() => UpdateInstaller.Extract(zipPath));

            if (sourceFolder is null)
            {
                UpdateStatusText.Text = "解压失败:下载到的文件不完整,请点「打开下载页」手动下载";
                return;
            }

            UpdateStatusText.Text = $"已准备好 {_availableUpdate.Tag}";

            MessageBoxResult answer = MessageBox.Show(
                this,
                $"即将关闭 CheatSheet,把程序目录替换成 {_availableUpdate.Tag},然后自动重新打开。\n\n" +
                $"被替换的目录:\n{UpdateInstaller.InstallFolder}\n\n现在就开始吗?",
                "安装更新",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.OK)
            {
                UpdateStatusText.Text = "已取消(新版本已下载好,随时可以再点「下载并更新」)";
                return;
            }

            UpdateStatusText.Text = "正在替换并重启…";

            if (!UpdateInstaller.ApplyAndRestart(sourceFolder, () => _main.Close()))
                UpdateStatusText.Text = "启动替换器失败(可能被安全软件拦了),请手动替换";
        }
        finally
        {
            InstallUpdateButton.IsEnabled = _availableUpdate is not null;
            DownloadButton.IsEnabled = _availableUpdate is not null;
        }
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e) => _main.ResetHotkeys();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingAction is null)
            return;

        if (e.Key == Key.Escape)
        {
            _recordingAction.Status = string.Empty;
            _recordingAction = null;

            // 取消录制也要把热键装回去。
            _main.CommitHotkeyChange();
            e.Handled = true;
            return;
        }

        // Alt 组合键在 WPF 里会把真正的按键放进 SystemKey。
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        string? gesture = HotkeyManager.GestureFrom(Keyboard.Modifiers, key);

        if (gesture is null)
        {
            _recordingAction.Status = "只按了修饰键,请再按一个普通按键";
            e.Handled = true;
            return;
        }

        _recordingAction.Gesture = gesture;
        _recordingAction.Status = string.Empty;
        _recordingAction = null;

        _main.CommitHotkeyChange();
        e.Handled = true;
    }
}
