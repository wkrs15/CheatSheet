using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CheatSheet;

/// <summary>
/// 一个可在设置面板里自定义的全局热键动作。
/// <para>
/// 它既是动作的元数据(稳定标识 / 显示名 / 默认手势),也是设置面板中那一行的视图模型:
/// <see cref="Gesture"/> 与 <see cref="Status"/> 变化时直接通知界面,省掉一层手写刷新。
/// </para>
/// </summary>
public sealed class HotkeyAction : INotifyPropertyChanged
{
    private string _gesture;
    private string _status = string.Empty;

    public HotkeyAction(string key, string name, string defaultGesture, Action callback)
    {
        Key = key;
        Name = name;
        DefaultGesture = defaultGesture;
        Callback = callback;
        _gesture = defaultGesture;
    }

    /// <summary>稳定标识,用于在 settings.json 中持久化(改显示名不会丢用户配置)。</summary>
    public string Key { get; }

    /// <summary>设置面板里显示的用途说明。</summary>
    public string Name { get; }

    public string DefaultGesture { get; }

    /// <summary>触发这个动作时执行的逻辑。</summary>
    public Action Callback { get; }

    /// <summary>当前手势,形如 <c>Ctrl+Alt+Space</c>。</summary>
    public string Gesture
    {
        get => _gesture;
        set
        {
            if (_gesture == value)
                return;

            _gesture = value;
            OnPropertyChanged();
        }
    }

    /// <summary>该行的状态说明(录制提示 / 被占用提示等)。</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
                return;

            _status = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
