using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CheatSheet;

/// <summary>
/// 轻量本地设置,持久化到 <c>%AppData%\CheatSheet\settings.json</c>。
/// 读写失败一律静默回退 —— 配置问题绝不该拦住播放。
/// </summary>
public sealed class AppSettings
{
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 800;
    public double WindowHeight { get; set; } = 460;
    public bool Topmost { get; set; } = true;
    public double WindowOpacity { get; set; } = 1.0;
    public double Volume { get; set; } = 0.8;
    public double SpeedRatio { get; set; } = 1.0;
    public bool Loop { get; set; }

    /// <summary>
    /// 暂停时怎么处理播放窗口:0 = 什么都不做,1 = 隐藏窗口,2 = 把画面压暗到最低。
    /// 默认 2 —— 暂停时画面自己退一边去,不挡着看游戏;继续播放时自动恢复。
    /// </summary>
    public int PauseBehavior { get; set; } = 2;

    /// <summary>拖动窗口边缘时是否按视频比例等比例缩放。</summary>
    public bool ProportionalResize { get; set; }
    public int SeekStepSeconds { get; set; } = 5;
    public string? LastDirectory { get; set; }

    /// <summary>
    /// 本地视频的断点续播:文件路径 → 上次看到的位置(秒)。
    /// <para>
    /// 键就是文件路径,所以文件被挪走 / 改名之后旧记录会失效 —— 保存时会顺手把
    /// 已经不在硬盘上的条目清掉(见 <c>PruneResumeEntries</c>)。
    /// </para>
    /// </summary>
    public Dictionary<string, double> Resume { get; set; } = new();

    /// <summary>是否记住本地视频的播放进度(下次打开同一个文件自动接着看)。</summary>
    public bool RememberPosition { get; set; } = true;

    /// <summary>启动时自动检查更新(一天最多查一次,见 MainWindow.AutoCheckUpdatesAsync)。</summary>
    public bool CheckUpdatesOnStart { get; set; } = true;

    /// <summary>上次检查更新的时间。用它实现"一天最多自动查一次"。</summary>
    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>网页模式打开时加载的首页。</summary>
    public string WebHomeUrl { get; set; } = "https://www.bilibili.com";

    /// <summary>上次关闭时是否停在网页模式(true = 网页,false = 本地视频)。</summary>
    public bool WebMode { get; set; }

    /// <summary>
    /// 网页视频开始播放后是否自动进入"网页全屏"(替用户点网站自己的网页全屏按钮,
    /// 比如 B 站的 <c>.bpx-player-ctrl-web</c>)。
    /// </summary>
    public bool AutoWebFullscreen { get; set; } = true;

    /// <summary>
    /// 用户自定义的全局热键:动作标识 → 手势字符串(如 <c>"Ctrl+Alt+Space"</c>)。
    /// 缺失的动作由代码里的默认手势补齐,所以升级新增动作时旧配置依然可用。
    /// </summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CheatSheet",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    loaded.Hotkeys ??= new Dictionary<string, string>();
                    loaded.Resume ??= new Dictionary<string, double>();
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置损坏(手改坏、被截断等)时直接回退默认值。
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch
        {
            // 磁盘只读 / 权限不足时忽略,不影响本次使用。
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
}
