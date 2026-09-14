using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;

namespace CheatSheet;

/// <summary>
/// 应用入口。主题与控件样式在 App.xaml 中通过合并 HandyControl 资源引入。
/// <para>
/// 这里另外管三件"进程级"的事,所以 <c>App.xaml</c> 里不再用 <c>StartupUri</c>
/// (窗口由 <see cref="OnStartup"/> 亲手创建):
/// <list type="number">
/// <item><b>单实例</b>:程序会常驻在屏幕顶端,起第二个实例只会多出一条控制条、
/// 热键还注册不上。第二个实例把"这次要打开的文件"通过命名管道交给第一个,然后自己退出。</item>
/// <item><b>崩溃兜底</b>:UI 线程的异常接住,弹个窗说清哪里出错、把详情写到日志,再干净退出
/// (不接的话系统会弹 WER 框,而且播放窗口会半死不活地留在屏幕最上层)。</item>
/// <item>用户名下第一个实例的命名管道服务(接收文件转发)。</item>
/// </list>
/// </para>
/// </summary>
public partial class App : Application
{
    // 名字里带 GUID:不与别的程序撞车。Local\ 前缀表示只在当前登录会话内可见。
    private const string InstanceMutexName = @"Local\CheatSheet.SingleInstance.9F1C0C42-2E4B-4C8E-9E1C-0B3E0A5E7D10";
    private const string ForwardPipeName = "CheatSheet.Forward.9F1C0C42-2E4B-4C8E-9E1C-0B3E0A5E7D10";

    private Mutex? _instanceMutex;
    private MainWindow? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);

        if (!TryBecomePrimary(_instanceMutex, createdNew))
        {
            ForwardToRunningInstance(e.Args);
            Shutdown();
            return;
        }

        HookCrashHandlers();

        _main = new MainWindow();
        MainWindow = _main;
        _main.Show();

        StartForwardServer();
    }

    /// <summary>
    /// 判断自己是不是"唯一的那一个"。
    /// <para>
    /// 上一次进程被强杀(任务管理器结束任务 / 断电)时,互斥量会处于"被遗弃"状态 ——
    /// 那种情况下它其实已经没有主人了,得再拿一次当主实例,否则强杀一次之后程序就再也起不来了。
    /// </para>
    /// </summary>
    private static bool TryBecomePrimary(Mutex mutex, bool createdNew)
    {
        if (createdNew)
            return true;

        try
        {
            return mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    // ---------------- 第二个实例:把文件交给第一个 ----------------

    private static void ForwardToRunningInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", ForwardPipeName, PipeDirection.Out);
            client.Connect(1500);

            using var writer = new StreamWriter(client);

            // 没有参数(用户只是又双击了一次图标):发个空消息,让第一个实例"冒个泡",
            // 免得用户以为程序压根没启动。
            writer.Write(string.Join('\n', args));
            writer.Flush();
        }
        catch
        {
            // 连不上通常是第一个实例正在退出 —— 那就当没这回事。
        }
    }

    /// <summary>常驻等第二个实例转发过来的文件(单实例的"另一半")。</summary>
    private void StartForwardServer()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        ForwardPipeName, PipeDirection.In, maxNumberOfServerInstances: 1);

                    await server.WaitForConnectionAsync();

                    using var reader = new StreamReader(server);
                    string payload = await reader.ReadToEndAsync();

                    string[] paths = payload.Split(
                        '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    await Dispatcher.InvokeAsync(() => _main?.OpenFilesFromOutside(paths));
                }
                catch
                {
                    // 管道出错(客户端中途断开等)不能让这个循环死掉。
                    await Task.Delay(500);
                }
            }
        });
    }

    // ---------------- 崩溃兜底 ----------------

    private void HookCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            // 标成已处理 = 这一步之后由我们决定怎么收场(提示 + 退出),
            // 而不是让系统弹一个 WER 框、顺便把播放窗口留在屏幕最上层。
            args.Handled = true;
            ReportFatal(args.Exception);
        };

        // 非 UI 线程的异常接不回来,只能留个记录(日志里能看到)。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                WriteCrashLog(ex);
        };
    }

    /// <summary>弹窗说清哪里出错,然后把程序关掉。</summary>
    private void ReportFatal(Exception exception)
    {
        string logPath = WriteCrashLog(exception);

        try
        {
            string message =
                "CheatSheet 遇到了一个错误,只能先关掉 —— 具体位置见下面这几行。\n\n" +
                Describe(exception);

            if (logPath.Length > 0)
                message += "\n\n完整信息已写到:\n" + logPath;

            if (_main is not null)
                MessageBox.Show(_main, message, "CheatSheet 出错了", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                MessageBox.Show(message, "CheatSheet 出错了", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // 连弹窗都失败(没有桌面会话等)就算了,反正下面要退出。
        }

        Shutdown(-1);
    }

    /// <summary>把异常压成几行"人话":类型 + 消息 + 调用栈最上面几帧。</summary>
    private static string Describe(Exception exception)
    {
        var lines = new List<string>
        {
            exception.GetType().Name + ": " + exception.Message
        };

        if (exception.InnerException is not null)
            lines.Add("内层:" + exception.InnerException.GetType().Name + ": " + exception.InnerException.Message);

        string[] stack = (exception.StackTrace ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lines.AddRange(stack.Take(6).Select(line => line.Trim()));

        return string.Join('\n', lines);
    }

    /// <summary>把完整异常写到 %AppData%\CheatSheet\crash-*.log,返回日志路径(失败返回空串)。</summary>
    private static string WriteCrashLog(Exception exception)
    {
        try
        {
            string directory = Path.GetDirectoryName(AppSettings.FilePath)!;
            Directory.CreateDirectory(directory);

            string path = Path.Combine(
                directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            File.WriteAllText(path,
                $"CheatSheet {typeof(App).Assembly.GetName().Version}" +
                $"{Environment.NewLine}{DateTime.Now:yyyy-MM-dd HH:mm:ss}" +
                $"{Environment.NewLine}{Environment.OSVersion}" +
                $"{Environment.NewLine}{Environment.NewLine}{exception}");

            return path;
        }
        catch
        {
            return string.Empty;
        }
    }
}
