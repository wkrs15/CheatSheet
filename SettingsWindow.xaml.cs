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

    private void Proportional_Click(object sender, RoutedEventArgs e)
        => _main.ProportionalResize = ProportionalBox.IsChecked == true;

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
