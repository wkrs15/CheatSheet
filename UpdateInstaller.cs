using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using HandyControl.Data;
using Growl = HandyControl.Controls.Growl;

namespace CheatSheet;

/// <summary>
/// 自动更新的"落地"部分:下载新包 → 解压 → 交给一个退出后才动手的替换器 → 重启。
/// <para>
/// 为什么必须有个替换器:Windows 上<b>正在运行的程序覆盖不了自己</b>(exe 和已加载的
/// DLL 都被锁着)。所以流程是"先把新文件准备好,再让一个外部脚本等我们退出,退出后替换、重启"。
/// 替换器用 PowerShell 的 <c>-EncodedCommand</c> 启动:命令是 Base64(UTF-16)传的,
/// 路径里有中文也不会乱码,而且不落 .ps1 文件、不受执行策略限制。
/// </para>
/// <para>
/// 目录也是刻意选的:<b>先解压到 %TEMP%,替换成功再把临时目录删掉</b> ——
/// 万一下载中断/解压失败/替换失败,原程序一个字节都没动过。
/// </para>
/// </summary>
internal static class UpdateInstaller
{
    /// <summary>下载和解压用的临时目录(退出后由替换器清理)。</summary>
    internal static string TempFolder { get; } =
        Path.Combine(Path.GetTempPath(), "CheatSheet-update");

    /// <summary>替换器写的结果文件:OK = 成功;其它 = 失败(下次启动会提示)。</summary>
    private static string ResultFile => Path.Combine(TempFolder, "update-result.txt");

    /// <summary>程序自己所在的目录(要被替换的那份)。</summary>
    internal static string InstallFolder { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>
    /// 程序所在目录能不能写。装在 <c>Program Files</c> 这类地方时没有写权限,
    /// 与其替换到一半失败,不如提前说清楚让用户手动更新。
    /// </summary>
    internal static bool CanWriteInstallDirectory()
    {
        try
        {
            string probe = Path.Combine(InstallFolder, ".cheatsheet-write-test");

            File.WriteAllText(probe, "1");
            File.Delete(probe);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 下载新版本的 zip,返回本地路径(失败返回 null)。进度是 0–100。
    /// </summary>
    internal static async Task<string?> DownloadAsync(
        UpdateChecker.UpdateInfo update, IProgress<double>? progress)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
            return null;

        try
        {
            Directory.CreateDirectory(TempFolder);

            string zipPath = Path.Combine(TempFolder, $"CheatSheet-{update.Tag}.zip");

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

            using HttpResponseMessage response =
                await client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
                return null;

            long? total = response.Content.Headers.ContentLength;

            await using Stream source = await response.Content.ReadAsStreamAsync();
            await using var target = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long read = 0;

            while (true)
            {
                int count = await source.ReadAsync(buffer);

                if (count <= 0)
                    break;

                await target.WriteAsync(buffer.AsMemory(0, count));
                read += count;

                if (total is > 0)
                    progress?.Report(Math.Clamp(read * 100.0 / total.Value, 0, 100));
            }

            // 明显不像个包(几百 KB 都不到)就当成失败,免得拿半个文件去替换。
            if (read < 100_000)
                return null;

            return zipPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把 zip 解压到一个干净的子目录,返回那个目录(失败返回 null)。
    /// 解压后会确认里面真有 CheatSheet.exe —— 下错文件/包不完整都在这儿拦住。
    /// </summary>
    internal static string? Extract(string zipPath)
    {
        try
        {
            string directory = Path.Combine(
                TempFolder, "extracted-" + Path.GetFileNameWithoutExtension(zipPath));

            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);

            Directory.CreateDirectory(directory);
            ZipFile.ExtractToDirectory(zipPath, directory, overwriteFiles: true);

            // 有的包会多套一层目录,往下找一层。
            string exe = Path.Combine(directory, "CheatSheet.exe");

            if (File.Exists(exe))
                return directory;

            foreach (string sub in Directory.GetDirectories(directory))
            {
                if (File.Exists(Path.Combine(sub, "CheatSheet.exe")))
                    return sub;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 启动替换器:它等我们退出后,把 <paramref name="sourceFolder"/> 里的文件覆盖到程序目录,
    /// 再重启程序。<paramref name="shutDown"/> 由调用方负责真的把程序关掉。
    /// </summary>
    internal static bool ApplyAndRestart(string sourceFolder, Action shutDown)
    {
        try
        {
            // 重启的是"我们自己这个 exe"(Environment.ProcessPath),不是拼出来的
            // CheatSheet.exe —— 用户把 exe 改过名的话,拼出来的路径是不存在的。
            string exePath = Environment.ProcessPath
                             ?? Path.Combine(InstallFolder, "CheatSheet.exe");

            string script = BuildPowerShellScript(
                sourceFolder, InstallFolder, exePath, Environment.ProcessId);

            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var startInfo = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (Process.Start(startInfo) is null)
                return false;

            shutDown();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 替换器脚本。要点:
    /// <list type="bullet">
    /// <item>先等目标进程退出(轮询 PID),再动手 —— 早一步文件还是锁着的;</item>
    /// <item>用 <c>robocopy</c> 覆盖:它对"文件被占用"自带重试,比 Copy-Item 稳;</item>
    /// <item>robocopy 的退出码 &lt; 8 才算成功(0–7 都是"复制完成"的不同含义);</item>
    /// <item>结果写进 update-result.txt,下次启动读它决定是提示还是清理;</item>
    /// <item>无论成败都把程序重新拉起来 —— 别让用户面对一个"更新完打不开"的空桌面。</item>
    /// </list>
    /// </summary>
    private static string BuildPowerShellScript(
        string source, string destination, string exePath, int processId)
    {
        return $@"
$ErrorActionPreference = 'SilentlyContinue'
while (Get-Process -Id {processId} -ErrorAction SilentlyContinue) {{ Start-Sleep -Milliseconds 250 }}
Start-Sleep -Milliseconds 700
& robocopy '{Escape(source)}' '{Escape(destination)}' /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP *> '{Escape(Path.Combine(TempFolder, "apply.log"))}'
$ok = $LASTEXITCODE -lt 8
Set-Content -LiteralPath '{Escape(ResultFile)}' -Value $(if ($ok) {{ 'OK' }} else {{ 'FAIL' }}) -Encoding UTF8
if ($ok) {{ Remove-Item -LiteralPath '{Escape(source)}' -Recurse -Force -ErrorAction SilentlyContinue }}
Start-Process -FilePath '{Escape(exePath)}'
";
    }

    /// <summary>PowerShell 单引号字符串里的单引号要写两遍。</summary>
    private static string Escape(string value) => value.Replace("'", "''");

    /// <summary>
    /// 启动时看一眼上次自动更新的结果:成功就把临时目录清掉,
    /// 失败就告诉用户"没换成,可以手动下载"(免得他以为更新过了)。
    /// </summary>
    internal static void ReportLastResult()
    {
        try
        {
            if (!File.Exists(ResultFile))
                return;

            // 结果文件带 UTF-8 BOM(Set-Content -Encoding UTF8 会写 BOM),
            // 顺手把可能残留的 BOM 和空白去掉再比,免得把成功看成失败。
            string result = File.ReadAllText(ResultFile)
                .Trim('\uFEFF', ' ', '\r', '\n', '\t')
                .ToUpperInvariant();

            try
            {
                Directory.Delete(TempFolder, recursive: true);
            }
            catch
            {
                // 临时目录里有东西没清掉不影响使用。
            }

            if (result != "OK")
            {
                Growl.Warning(new GrowlInfo
                {
                    Message = "上次自动更新没有成功,程序还是旧版本 —— 可以在 设置 → 关于 里手动下载。",
                    WaitTime = 8
                });
            }
        }
        catch
        {
            // 结果文件坏了也只是少个提示。
        }
    }
}
