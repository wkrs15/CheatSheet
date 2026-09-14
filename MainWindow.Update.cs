using System.Diagnostics;
using HandyControl.Data;
using Growl = HandyControl.Controls.Growl;

namespace CheatSheet;

/// <summary>
/// 检查更新 + 打开下载页 / 配置目录。
/// <para>
/// 程序是绿色版:自己覆盖不了正在运行的自己,所以做的是"发现新版本 → 把下载页递给你",
/// 而不是静默自更新(那种得再写一个替换用的辅助进程,收益不值这个复杂度)。
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>界面上显示的版本号(如 v0.7.0)。</summary>
    internal string VersionLabel => "v" + UpdateChecker.CurrentVersion.ToString(3);

    /// <summary>启动时自动检查更新(一天最多查一次)。</summary>
    internal bool CheckUpdatesOnStart
    {
        get => _settings.CheckUpdatesOnStart;
        set
        {
            if (_settings.CheckUpdatesOnStart == value)
                return;

            _settings.CheckUpdatesOnStart = value;
            _settings.Save();
        }
    }

    /// <summary>检查结果:成功与否 + 有没有新版本(成功但没有新版本时 Update 为 null)。</summary>
    internal sealed record UpdateCheckResult(bool Succeeded, UpdateChecker.UpdateInfo? Update);

    /// <summary>
    /// 查一次最新版本。<see cref="UpdateCheckResult.Succeeded"/> 为 false 表示"没查到"
    /// (断网 / 代理不通 / 被限流)—— 界面上要和"已经是最新"区分开,别骗用户。
    /// </summary>
    internal static async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        UpdateChecker.UpdateInfo? info = await UpdateChecker.FetchLatestAsync();

        if (info is null)
            return new UpdateCheckResult(false, null);

        return UpdateChecker.IsNewer(info.Version)
            ? new UpdateCheckResult(true, info)
            : new UpdateCheckResult(true, null);
    }

    /// <summary>
    /// 启动后顺手查一次(用户开了自动检查、且距上次超过一天)。
    /// 只在真的发现新版本时冒一条提示,平时完全静默。
    /// </summary>
    private async Task AutoCheckUpdatesAsync()
    {
        if (!_settings.CheckUpdatesOnStart)
            return;

        if (_settings.LastUpdateCheck is DateTime last && (DateTime.Now - last).TotalHours < 24)
            return;

        _settings.LastUpdateCheck = DateTime.Now;
        _settings.Save();

        try
        {
            // 先把窗口摆好再说别的(刚启动时 Growl 宿主还没就位)。
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 窗口在延迟里关掉了:那就别查了。
            return;
        }

        UpdateCheckResult result = await CheckForUpdatesAsync();

        if (result.Update is not null)
            Growl.Info($"发现新版本 {result.Update.Tag} —— 在 设置 → 关于 里打开下载页", "update");
    }

    /// <summary>用系统默认浏览器打开链接。</summary>
    internal static void OpenInBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Growl.Error(new GrowlInfo { Message = "打不开浏览器: " + ex.Message, WaitTime = 4 });
        }
    }

    /// <summary>在资源管理器里打开配置目录(设置和崩溃日志都在那儿)。</summary>
    internal static void OpenConfigFolder()
    {
        try
        {
            string directory = System.IO.Path.GetDirectoryName(AppSettings.FilePath)!;
            System.IO.Directory.CreateDirectory(directory);

            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Growl.Error(new GrowlInfo { Message = "打不开配置目录: " + ex.Message, WaitTime = 4 });
        }
    }
}
