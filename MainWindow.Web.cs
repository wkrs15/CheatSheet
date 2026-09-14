using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HandyControl.Data;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Growl = HandyControl.Controls.Growl;

namespace CheatSheet;

/// <summary>
/// <see cref="MainWindow"/> 的"网页模式":内置浏览器播放网页视频(如 B 站攻略)。
/// <para>
/// 与本地视频模式互斥 —— 同一时刻只显示其中一个。网页画面能跟着窗口一起半透明,
/// 是因为这里用的是 <c>WebView2CompositionControl</c>(走 DirectComposition 参与 WPF 合成),
/// 而不是传统的 HwndHost 版 WebView2(那种在分层透明窗口里根本渲染不出来)。
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>当前是否处于网页模式。</summary>
    private bool _webMode;

    /// <summary>WebView2 是否已经初始化过(初始化很贵,一次网页模式里只做一次)。</summary>
    private bool _webInitialized;

    /// <summary>
    /// 这一次网页模式用的 WebView。它是<b>按需创建、退出即销毁</b>的:
    /// 背后是一整组 Edge 进程(动辄几百 MB)加上后台的 GPU / 网络活动,
    /// 退出网页模式只把它折叠起来的话,这些资源会一直白占着 —— 而这个程序的使用场景
    /// 恰恰是"边玩游戏边看",资源空不出来是要挨骂的。
    /// <para>
    /// 之所以不能"留着实例下次复用":WebView2 的 <c>Dispose()</c> 之后同一个实例
    /// 就不能再初始化了(会抛 ObjectDisposedException),所以每次进网页模式都 new 一个新的。
    /// </para>
    /// </summary>
    private WebView2CompositionControl? WebView;

    /// <summary>页面里有没有 &lt;video&gt;。没有就不算"暂停",免得窗口莫名其妙变暗。</summary>
    private bool _webHasVideo;

    /// <summary>页面里的 &lt;video&gt; 是不是在播。</summary>
    private bool _webVideoPlaying;

    /// <summary>
    /// 页面里的视频有没有真正播过一次。没播过不触发"暂停时"行为 ——
    /// 和 v0.5 修本地视频那条约定一致:页面刚打开、B 站还在等用户点播放,不叫暂停。
    /// </summary>
    private bool _webPlayedOnce;

    /// <summary>页面里 &lt;video&gt; 的实际倍速(轮询得来,长按快进结束时还原到它)。</summary>
    private double _webSpeed = 1.0;

    /// <summary>页面里 &lt;video&gt; 的当前播放位置 / 总时长(秒),进度条显示的就是它。</summary>
    private double _webPosition;
    private double _webDuration;

    /// <summary>我们刚把"按下"喂给网页了,抬起也要照喂 —— 哪怕这时候鼠标已经挪到进度条上面。</summary>
    private bool _webButtonDown;

    /// <summary>轮询页面状态是异步的,加个闸防止重入。</summary>
    private bool _webStatePolling;

    /// <summary>
    /// "这一轮没读到视频"的连续次数。
    /// <para>
    /// 换P、换源、广告切换的一瞬间页面里那个 &lt;video&gt; 会读不到(或时长是 0),
    /// 但那是暂时的 —— 只读不到一次就把 <c>_webHasVideo</c> 清掉的话,
    /// "暂停压暗 / 暂停时隐藏"会被撤销、下一轮又加上,画面上就是闪一下。
    /// 所以连着 <see cref="WebMissingPollTolerance"/> 轮都读不到才认账。
    /// </para>
    /// </summary>
    private int _webMissingPolls;

    /// <summary>连续多少轮读不到视频才认为"页面里真的没有视频"(200ms 一轮 ≈ 1 秒)。</summary>
    private const int WebMissingPollTolerance = 5;

    /// <summary>这一轮没读到视频:连续几轮都读不到才把状态清成"没有视频"。</summary>
    private void ForgetVideoAfterMissingPolls()
    {
        if (++_webMissingPolls < WebMissingPollTolerance)
            return;

        _webHasVideo = false;
        _webVideoPlaying = false;
        _webPosition = 0;
        _webDuration = 0;
    }

    /// <summary>页面当前地址(控制条上的地址栏显示它)。</summary>
    private string _webCurrentUrl = string.Empty;

    /// <summary>页面自己的标题(控制条在网页模式下显示它,见 <c>WebLabel</c>)。</summary>
    private string _webTitle = string.Empty;

    /// <summary>WebView 还没初始化好时,地址栏输入的地址先寄存在这。</summary>
    private string? _pendingWebUrl;

    /// <summary>反射拿到的 <c>WebView2CompositionControl.SendMouseInput</c>(SDK 里是 private)。</summary>
    private MethodInfo? _sendMouseInput;

    /// <summary>已经为"鼠标转发不可用"报过一次警,别每次鼠标移动都刷一屏。</summary>
    private bool _mouseForwardWarned;

    internal bool IsWebMode => _webMode;

    /// <summary>页面当前地址。</summary>
    internal string WebCurrentUrl => _webCurrentUrl;

    /// <summary>
    /// 切到网页模式。优先回到**上次那个页面** —— 退出网页模式会把 WebView 整个销毁,
    /// 回来本来就是重新加载,再回首页的话你追的番就白追了。首页只在没来过任何页面时用。
    /// </summary>
    internal void EnterWebMode()
        => EnterWebMode(string.IsNullOrWhiteSpace(_webCurrentUrl) ? _settings.WebHomeUrl : _webCurrentUrl);

    /// <summary>切到网页模式并打开指定地址。</summary>
    internal void EnterWebMode(string? url)
    {
        // 两个模式不共存:进网页前先停掉本地视频;上一个模式留下的
        // "暂停压暗 / 暂停隐藏"也一起撤掉,免得带进网页模式。
        if (_isPlaying)
        {
            Player.Pause();
            _isPlaying = false;

            // 本地视频停在半路:把位置记下来,下次切回来接着看。
            SaveResumePosition(persist: true);
        }

        ResetPauseEffects();

        _webMode = true;
        _webCurrentUrl = url ?? _settings.WebHomeUrl;

        // 还没拿到网页标题之前,控制条显示"浏览器模式" —— 不能留着上一条本地视频的文件名。
        _webTitle = string.Empty;

        // 这个轮询定时器现在兼职盯着页面里的 <video>(在播没在播、倍速多少),
        // 所以网页模式下不能停 —— 以前这里会 _timer.Stop(),"暂停自动变暗"因此永远不触发。
        _timer.Start();

        ApplyWebModeVisibility();
        _ = EnsureWebViewAsync(url);

        RaiseStateChanged();
    }

    /// <summary>回到本地视频模式。</summary>
    internal void ExitWebMode()
    {
        if (!_webMode)
            return;

        _webMode = false;
        _webHasVideo = false;
        _webVideoPlaying = false;
        _webPlayedOnce = false;
        _webButtonDown = false;
        _webPosition = 0;
        _webDuration = 0;
        _webMissingPolls = 0;

        // 下次进来是新页面,选集列表和网页标题都重新来。
        _webTitle = string.Empty;
        ClearWebParts();

        // 控制条 / 窗口标题改回本地那一套(本地视频还开着的话,文件名接着显示)。
        if (_index >= 0 && _index < _playlist.Count)
            UpdateFileName();
        else
            Title = "CheatSheet";

        ResetPauseEffects();
        ApplyWebModeVisibility();

        // 网页的声音停掉,免得退出后还在后台响。
        ApplyMuteStateToWeb(forceMute: true);

        // 然后把 WebView 整个收掉:Edge 那组进程、GPU、网络全释放。
        // (上面那句静音留着是为了"销毁万一失败"时不至于还在后台出声。)
        ReleaseWebView();

        RaiseStateChanged();
    }

    /// <summary>
    /// 设置面板里直接选模式:true = 浏览器模式,false = 本地视频模式。
    /// (以前还有个 <c>Ctrl+Alt+B</c> 的全局热键来回切,已经去掉了 —— 改模式现在只走设置面板。)
    /// </summary>
    internal void SetWebMode(bool web)
    {
        if (web == _webMode)
            return;

        if (web)
            EnterWebMode();
        else
            ExitWebMode();
    }

    /// <summary>浏览器模式的首页地址(设置面板里可改)。</summary>
    internal string WebHomeUrl
    {
        get => _settings.WebHomeUrl;
        set
        {
            string url = (value ?? string.Empty).Trim();

            if (_settings.WebHomeUrl == url)
                return;

            _settings.WebHomeUrl = url;
            _settings.Save();
        }
    }

    /// <summary>打开一个网址(不在网页模式则先切过去)。地址栏和设置里的首页都走这里。</summary>
    internal void OpenWebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        url = url.Trim();

        if (!_webMode)
        {
            EnterWebMode(url);
            return;
        }

        _webCurrentUrl = url;
        RaiseStateChanged();

        if (!_webInitialized)
        {
            // WebView 还在初始化:先记下来,等它好了再导航(否则这会话会被丢掉)。
            _pendingWebUrl = url;
            return;
        }

        CoreWebView2? core = WebView?.CoreWebView2;

        if (core is not null)
        {
            try
            {
                core.Navigate(NormalizeUrl(url));
            }
            catch (Exception ex)
            {
                Growl.Error(new GrowlInfo { Message = "打不开这个网址: " + ex.Message, WaitTime = 4 });
            }
        }
    }

    /// <summary>按模式切换各个视图的显隐。</summary>
    private void ApplyWebModeVisibility()
    {
        // WebView 不用管显隐 —— 它只在网页模式下存在(退出时整个销毁)。
        // 网页模式整块画面都归浏览器,所以要给它留一条专门拖窗口的"标题栏"。
        WebDragStrip.Visibility = _webMode ? Visibility.Visible : Visibility.Collapsed;

        // 进度条两种模式都要:网页模式下它的数据来自轮询页面里那个 <video>(见 RefreshProgress)。
        // 以前这里是"网页自己带播放控件,所以只在本地视频模式显示"—— 但 B 站那套控件要鼠标悬停才出来,
        // 平常看不到位置,不如我们自己那条一直在。
        BottomBar.Visibility = Visibility.Visible;

        if (_webMode)
        {
            HintText.Visibility = Visibility.Collapsed;
        }
        else
        {
            HintText.Visibility = Player.Source is null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async Task EnsureWebViewAsync(string? url)
    {
        if (_webInitialized && WebView is not null)
        {
            if (!string.IsNullOrWhiteSpace(url))
                NavigateWeb(url);
            ApplyMuteStateToWeb();
            return;
        }

        // 上一次退出网页模式时把控件销毁掉了(Dispose 过的实例不能再初始化),
        // 所以这里重新 new 一个挂到宿主里。
        if (WebView is null)
        {
            WebView = new WebView2CompositionControl
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            WebHost.Children.Add(WebView);
        }

        try
        {
            await WebView.EnsureCoreWebView2Async();

            CoreWebView2? core = WebView.CoreWebView2;

            if (core is null)
                return;

            // 关掉浏览器自带的右键菜单/状态栏/DevTools,让它更像"播放器"而不是浏览器。
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;

            // 网页里的"新窗口打开"直接在原地导航,不开新的宿主窗口。
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                NavigateWeb(e.Uri);
            };

            core.NavigationCompleted += (_, e) => OnWebNavigationCompleted(e);

            // 页面标题(控制条上那行字):站内切换分P 时它可能不变,但换视频一定会变。
            core.DocumentTitleChanged += (_, _) =>
            {
                _webTitle = core.DocumentTitle ?? string.Empty;
                UpdateWebLabel();
            };

            // 站内跳转(SPA)不一定触发 NavigationCompleted,地址栏要跟着刷新。
            core.SourceChanged += (_, _) =>
            {
                try
                {
                    _webCurrentUrl = core.Source;
                    RaiseStateChanged();
                }
                catch
                {
                    // 正在拆控件时可能抛 ObjectDisposedException,忽略。
                }
            };

            _webInitialized = true;

            // 地址栏比 WebView 准备好得更早的话,用用户刚输入的那个地址。
            string? target = _pendingWebUrl ?? url;
            _pendingWebUrl = null;

            if (!string.IsNullOrWhiteSpace(target))
                NavigateWeb(target);
        }
        catch (Exception ex)
        {
            Growl.Error(new GrowlInfo
            {
                Message = "网页模式初始化失败: " + ex.Message + "(需要 Windows 的 WebView2 运行时)",
                WaitTime = 6
            });
        }
    }

    private void NavigateWeb(string url)
    {
        CoreWebView2? core = _webInitialized ? WebView?.CoreWebView2 : null;

        if (core is null)
            return;

        try
        {
            core.Navigate(NormalizeUrl(url));
        }
        catch (Exception ex)
        {
            Growl.Error(new GrowlInfo { Message = "打不开这个网址: " + ex.Message, WaitTime = 4 });
        }
    }

    private void OnWebNavigationCompleted(Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        // 导航换了文档,上一次的自动全屏状态就作废了,得在新页面里重新来一遍。
        ApplyMuteStateToWeb();

        // 新文档:页面状态先按"还没有视频"处理,等第一次轮询拿到真实值。
        _webHasVideo = false;
        _webVideoPlaying = false;
        _webPlayedOnce = false;
        _webButtonDown = false;
        _webPosition = 0;
        _webDuration = 0;
        _webMissingPolls = 0;

        if (e.IsSuccess)
        {
            _webCurrentUrl = WebView?.CoreWebView2?.Source ?? _webCurrentUrl;

            // 新页面里的 <video> 还不知道我们设了倍速,重新写一次。
            WebSetSpeed(_speedRatio);
        }

        // 换了文档:上一个视频的选集列表立刻作废(下拉栏不能还挂着它的分P),
        // 新页面里有没有选集、有哪些,交给下一轮读取去认。
        ClearWebParts();

        RaiseStateChanged();

        if (_settings.AutoWebFullscreen && e.IsSuccess)
            _ = AutoWebFullscreenAsync();
    }

    // ---------------- 页面状态轮询 ----------------

    /// <summary>
    /// 问一下页面里那个 &lt;video&gt; 现在什么状态(有没有、在播没在播、倍速多少)。
    /// <para>
    /// 网页模式下 app 看不到视频,而"暂停变暗/隐藏""长按 2 倍速"都依赖这个状态,
    /// 页面也没有事件能推给我们,所以只能按轮询定时器(:200ms)主动问。
    /// </para>
    /// </summary>
    internal void PollWebState() => _ = PollWebStateAsync();

    private async Task PollWebStateAsync()
    {
        if (!_webMode || !_webInitialized || _webStatePolling)
            return;

        CoreWebView2? core = WebView?.CoreWebView2;

        if (core is null)
            return;

        _webStatePolling = true;

        string script = "(() => { " + PickMainVideoJs + @"
    const v = mainVideo();
    if (!v) return null;
    return {
        p: v.paused,
        t: v.currentTime,
        r: v.playbackRate,
        d: isFinite(v.duration) ? v.duration : 0
    };
})();";

        try
        {
            // 先在轮询里兜一道地址:页面自己切分P 是 SPA 内部跳转(B 站用 history API),
            // 不一定触发 NavigationCompleted / SourceChanged,地址一变这里就能跟上,
            // 下拉栏的"当前第几P"也就不会停在旧值上。比较字符串而已,很便宜。
            string current = core.Source;

            if (!string.Equals(current, _webCurrentUrl, StringComparison.Ordinal))
            {
                _webCurrentUrl = current;
                RaiseStateChanged();
            }

            // 选集列表按间隔读一遍:它是页面自己渲染的,而且站内切集不会触发导航事件,
            // 只能定期去看(每秒一次足够了,毕竟只是读几个 DOM 节点的文字)。
            if ((DateTime.UtcNow - _webPartsReadAt).TotalSeconds >= 1)
            {
                _webPartsReadAt = DateTime.UtcNow;
                await ReadWebPartsAsync(core);
            }

            string json = await core.ExecuteScriptAsync(script);

            // 页面里没有 <video>(比如 B 站首页)—— 这不算"暂停"。
            // 但换P / 换源 / 广告的一瞬间也会读不到,所以"读不到"要连着几次才认账:
            // 一读不到就清状态的话,暂停压暗会被撤销、下一轮又压回去,画面闪一下。
            if (string.IsNullOrEmpty(json) || json == "null")
            {
                ForgetVideoAfterMissingPolls();
                return;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            System.Text.Json.JsonElement root = doc.RootElement;

            double duration = root.GetProperty("d").GetDouble();

            // 时长读不出来同样是"换源中"的典型状态(v.duration 这会儿是 NaN/0),
            // 一样按"暂时读不到"处理。
            if (duration <= 0.01)
            {
                ForgetVideoAfterMissingPolls();
                return;
            }

            _webMissingPolls = 0;
            _webDuration = duration;
            _webHasVideo = true;
            _webVideoPlaying = !root.GetProperty("p").GetBoolean();
            _webPosition = root.GetProperty("t").GetDouble();

            if (_webVideoPlaying)
                _webPlayedOnce = true;

            double rate = root.GetProperty("r").GetDouble();
            if (rate > 0.01)
                _webSpeed = rate;
        }
        catch
        {
            // 页面正在切换、WebView 正在重建:先当"这一轮没读到",别急着把状态清掉
            // (清了就等于撤销暂停压暗 / 撤掉"暂停时隐藏",下一轮又加回来 —— 画面会闪)。
            ForgetVideoAfterMissingPolls();
        }
        finally
        {
            _webStatePolling = false;
        }
    }

    /// <summary>把倍速写进页面里的 &lt;video&gt;(网页模式下"倍速/长按快进"都落到这里)。</summary>
    internal void WebSetSpeed(double ratio)
    {
        if (!_webMode)
            return;

        string script = "(() => { " + PickMainVideoJs +
            " const v = mainVideo();" +
            $" if (v) {{ v.playbackRate = {FormatScriptNumber(ratio)}; }} }})();";

        _ = ExecuteWebScriptAsync(script);
    }

    /// <summary>脚本里的数字一律用小数点(某些语言区域会用逗号,那样注入进去是语法错误)。</summary>
    private static string FormatScriptNumber(double value)
        => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---------------- 网页模式:手动转发鼠标 + 拖窗口 ----------------

    /// <summary>网页模式:光标在画面上按下。顶部那条留给"拖窗口",底部那条留给进度条,其余一律喂给网页。</summary>
    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_webMode)
            return;

        Point position = e.GetPosition(this);

        // 顶部 28px 是"标题栏"。用 Height 不用 ActualHeight:XAML 里是写死的尺寸,不受布局时机影响。
        if (position.Y <= WebDragStrip.Height)
        {
            // 这一下必须拦掉,不能让事件继续走到 WebView —— 浏览器按下会自己 SetCapture,
            // 之后 DragMove 内部的系统移动循环就拿不到鼠标了(表现:按住了窗口一动不动)。
            e.Handled = true;

            _dragOrigin = position;
            _dragCandidate = true;
            return;
        }

        // 底部那条是 app 自己的进度条:交给 WPF,别喂给网页(否则拖进度条会顺带点到网页里的东西)。
        if (IsOverBottomBar(position))
            return;

        if (ForwardWebMouse(CoreWebView2MouseEventKind.LeftButtonDown, e))
        {
            _webButtonDown = true;
            e.Handled = true;
        }
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_webMode)
            return;

        // 在顶部那条按下的那一次,网页压根没收到过"按下",当然也不该收到"抬起"。
        if (_dragCandidate)
        {
            _dragCandidate = false;
            e.Handled = true;
            return;
        }

        // 只要"按下"给过网页,这一步就得给(不管鼠标现在飘到哪儿了),否则网页那边的拖动会卡住。
        if (!_webButtonDown && IsOverBottomBar(e.GetPosition(this)))
            return;

        _webButtonDown = false;

        if (ForwardWebMouse(CoreWebView2MouseEventKind.LeftButtonUp, e))
            e.Handled = true;
    }

    private void Window_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ForwardWebMouse(CoreWebView2MouseEventKind.RightButtonDown, e))
            e.Handled = true;
    }

    /// <summary>右键抬起。主窗口那边顺手会把 WPF 自己的右键菜单压掉。</summary>
    internal void ForwardWebRightButtonUp(MouseEventArgs e)
        => ForwardWebMouse(CoreWebView2MouseEventKind.RightButtonUp, e);

    /// <summary>
    /// 网页模式:移动 / 拖动。故意<b>不</b>用 <c>CaptureMouse</c> —— 窗口级 Preview 事件本来就会
    /// 跟着鼠标走(哪怕移出了顶部那条窄带),自己抓着捕获反而会跟 <c>DragMove</c> 的系统移动循环抢鼠标。
    /// </summary>
    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_webMode)
            return;

        // 正在拖窗口:这一段全归我们;网页没收到过"按下",也不用喂。
        if (_dragCandidate)
        {
            Video_MouseMove(sender, e);
            return;
        }

        // 网页那边正按着(在拖进度条之类),那不管鼠标现在在哪儿都要继续喂 ——
        // 中间断掉的话,网页那边的拖动会卡在半路。
        if (!_webButtonDown && IsOverBottomBar(e.GetPosition(this)))
            return;

        if (ForwardWebMouse(CoreWebView2MouseEventKind.Move, e))
            e.Handled = true;
    }

    /// <summary>鼠标是不是落在画面底部那条进度条上(那是我们自己的控件,不该喂给网页)。</summary>
    private bool IsOverBottomBar(Point position)
        => BottomBar.Visibility == Visibility.Visible
           && ActualHeight > 1
           && position.Y >= ActualHeight - BottomBar.ActualHeight;

    /// <summary>
    /// 把鼠标事件手动喂给 WebView2。
    /// <para>
    /// 控件本来会自己转发(它重写了 OnMouseDown/Up/Move/Wheel,内部调 <c>SendMouseInput</c>),
    /// 但那套转发的前提是"命中测试选中了它"。实测在这个分层透明窗口里没选中:鼠标全被
    /// <c>VideoArea</c> 接走,浏览器于是毫无反应,表现就是"操控不了浏览器"。所以这里在窗口级
    /// Preview 事件里直接喂过去 —— Preview 从根往下隧道,必定先经过窗口这一层,不受命中测试影响;
    /// 喂完再把事件拦掉,免得控件又转发一次(一次点击变两下)。
    /// </para>
    /// <para>
    /// 注意 <c>SendMouseInput</c> 在 SDK 里是 <b>private</b> 的(控件自己用的),这里反射拿它出来调 ——
    /// 这是唯一能自己发鼠标的入口(<c>CoreWebView2CompositionController</c> 也没暴露)。
    /// 它的坐标参数是 <c>System.Drawing.Point</c>,也就是<b>物理像素</b>(和控制器 Bounds 同一套单位),
    /// 所以要把 WPF 的 DIP 乘上 DPI 缩放再传进去 —— 这一步搞错在 125% 缩放的屏上就会点偏。
    /// csproj 里包版本是钉死的,反射的风险可控;万一哪天升级 SDK 名字变了,这里会安静地失效,
    /// 表现就退回"网页点不动"。
    /// </para>
    /// <para>
    /// 滚轮刻意<b>不</b>转发:画面上滚轮调 app 音量是既定操作(README 里写的),留给本地那套。
    /// </para>
    /// </summary>
    private bool ForwardWebMouse(CoreWebView2MouseEventKind kind, MouseEventArgs e, uint mouseData = 0)
    {
        WebView2CompositionControl? view = _webMode && _webInitialized ? WebView : null;

        if (view is null || view.CoreWebView2 is null)
            return false;

        _sendMouseInput ??= typeof(WebView2CompositionControl).GetMethod(
            "SendMouseInput",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(CoreWebView2MouseEventKind),
                typeof(CoreWebView2MouseEventVirtualKeys),
                typeof(uint),
                typeof(System.Drawing.Point)
            ],
            modifiers: null);

        if (_sendMouseInput is null)
        {
            // 反射拿不到 = 鼠标转发彻底不可用,网页会像"点不动"。这种情况必须说出来,
            // 不然用户只会觉得"网页又坏了",而这里静默返回是什么线索都留不下的。
            if (!_mouseForwardWarned)
            {
                _mouseForwardWarned = true;

                Growl.Warning(new GrowlInfo
                {
                    Message = "网页模式的鼠标转发不可用(WebView2 版本变了吗?),网页里的点击暂时无效。",
                    WaitTime = 6
                });
            }

            return false;
        }

        try
        {
            Point dip = e.GetPosition(view);
            DpiScale dpi = VisualTreeHelper.GetDpi(view);

            var physical = new System.Drawing.Point(
                (int)Math.Round(dip.X * dpi.DpiScaleX),
                (int)Math.Round(dip.Y * dpi.DpiScaleY));

            _sendMouseInput.Invoke(view,
                [kind, CurrentMouseVirtualKeys(), mouseData, physical]);

            return true;
        }
        catch
        {
            // 控件还没准备好 / 正在重建时忽略。
            return false;
        }
    }

    /// <summary>把"现在哪些键是按着的"翻成浏览器那套位标志(就是 Win32 的 MK_*)。</summary>
    private static CoreWebView2MouseEventVirtualKeys CurrentMouseVirtualKeys()
    {
        var keys = CoreWebView2MouseEventVirtualKeys.None;

        if (Mouse.LeftButton == MouseButtonState.Pressed)
            keys |= CoreWebView2MouseEventVirtualKeys.LeftButton;
        if (Mouse.RightButton == MouseButtonState.Pressed)
            keys |= CoreWebView2MouseEventVirtualKeys.RightButton;
        if (Mouse.MiddleButton == MouseButtonState.Pressed)
            keys |= CoreWebView2MouseEventVirtualKeys.MiddleButton;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            keys |= CoreWebView2MouseEventVirtualKeys.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            keys |= CoreWebView2MouseEventVirtualKeys.Control;

        return keys;
    }

    /// <summary>把静音状态同步给网页(退出网页模式时强制静音)。</summary>
    private void ApplyMuteStateToWeb(bool forceMute = false)
    {
        try
        {
            CoreWebView2? core = _webInitialized ? WebView?.CoreWebView2 : null;

            if (core is not null)
                core.IsMuted = forceMute || !_webMode || _muted;
        }
        catch
        {
            // 控件还没准备好时忽略。
        }
    }

    // ---------------- 网页模式下的热键动作 ----------------

    /// <summary>
    /// 每段脚本都要"找到页面里那个主视频"。一个页面上可能有好几个 &lt;video&gt;
    /// (预加载的、悬停预览的、广告的),<c>querySelector('video')</c> 拿到的是文档里第一个,
    /// 经常不是用户正在看的那个 —— 表现就是"热键按了没反应"。
    /// 这里统一取"可见面积最大"的那个:主播放器的画面一定最大。
    /// </summary>
    private const string PickMainVideoJs = @"const mainVideo = () => {
        let best = null, area = 0;
        for (const v of document.querySelectorAll('video')) {
            const r = v.getBoundingClientRect();
            const a = r.width * r.height;
            if (a > area) { best = v; area = a; }
        }
        return best;
    };";

    /// <summary>网页模式的 播放 / 暂停:直接操作页面里的 &lt;video&gt;。</summary>
    internal void WebTogglePlayPause()
    {
        if (!_webMode)
            return;

        string script = "(() => { " + PickMainVideoJs +
            " const v = mainVideo(); if (v) { v.paused ? v.play() : v.pause(); } })();";

        _ = ExecuteWebScriptAsync(script);

        // 先把状态按"反过来了"记一笔:暂停变暗/隐藏和播放图标都不用干等下一次轮询。
        if (_webHasVideo)
        {
            _webVideoPlaying = !_webVideoPlaying;

            if (_webVideoPlaying)
                _webPlayedOnce = true;

            UpdatePauseBehavior();
            RaiseStateChanged();
        }
    }

    /// <summary>网页模式的 快进 / 快退(相对当前位置)。</summary>
    internal void WebSeekBy(double seconds) => SeekWebVideo(seconds, relative: true);

    /// <summary>把播放位置写到页面里的 &lt;video&gt;(进度条拖动走这里,给的是绝对秒数)。</summary>
    internal void WebSeekTo(double seconds) => SeekWebVideo(seconds, relative: false);

    private void SeekWebVideo(double seconds, bool relative)
    {
        if (!_webMode)
            return;

        // 先把进度按"已经跳过去了"记一笔,进度条不用干等下一次轮询(最多 200ms)。
        if (_webDuration > 0.01)
            _webPosition = Math.Clamp(relative ? _webPosition + seconds : seconds, 0, _webDuration);

        string script = "(() => { " + PickMainVideoJs +
            " const v = mainVideo(); if (!v) return;" +
            " const max = isFinite(v.duration) ? v.duration : 0;" +
            (relative
                ? " const t = v.currentTime + " + FormatScriptNumber(seconds) + ";"
                : " const t = " + FormatScriptNumber(seconds) + ";") +
            " v.currentTime = max > 0 ? Math.min(Math.max(t, 0), max) : Math.max(t, 0);" +
            " })();";

        _ = ExecuteWebScriptAsync(script);
    }

    /// <summary>
    /// 网页模式下把"当前看的是什么"同步给控制条和窗口标题。
    /// <para>
    /// 标题是现算的(见 <c>FileLabel</c> / <c>WebLabel</c>),所以这里只需要把窗口标题也刷一遍
    /// 再通知界面 —— 不然网页模式下任务栏里还挂着本地那个文件名。
    /// </para>
    /// </summary>
    private void UpdateWebLabel()
    {
        if (!_webMode)
            return;

        Title = $"{FileLabel} - CheatSheet";
        RaiseStateChanged();
    }

    /// <summary>把一段脚本丢给页面执行。不需要返回值时用它(要返回值得用 ExecuteScriptAsync)。</summary>
    private async Task ExecuteWebScriptAsync(string script)
    {
        CoreWebView2? core = _webInitialized ? WebView?.CoreWebView2 : null;

        if (core is null)
            return;

        try
        {
            await core.ExecuteScriptAsync(script);
        }
        catch
        {
            // 页面还没加载好时忽略。
        }
    }

    // ---------------- 播放器自己的选集列表(控制条上的「选集」下拉栏) ----------------

    /// <summary>页面里"播放器自己的选集列表"里每一项的 CSS 选择器(B 站 2026-09 实测)。</summary>
    private const string PartItemSelector = ".bpx-player-ctrl-eplist-multi-menu-item";

    /// <summary>
    /// 页面里选集列表的标题(第 0 项 = 第 1 集)。
    /// <para>
    /// 列表是从<b>页面 DOM</b> 读的,不是从接口取的 —— 因为切集也要点这一项:
    /// B 站的选集项点下去是站内切换(改 history + 换播放源),页面不重载,
    /// 所以不会像"改网址导航"那样把画面整个切出去重来一遍。
    /// </para>
    /// </summary>
    private readonly List<string> _webParts = new();

    /// <summary>当前在播的是第几项(读页面里的高亮项得到;-1 = 没读到)。</summary>
    private int _webCurrentPart = -1;

    /// <summary>列表内容 + 当前项拼的签名:变了才通知界面刷新(否则 5 次/秒地重建下拉栏)。</summary>
    private string _webPartsSignature = string.Empty;

    /// <summary>上次读列表的时间。</summary>
    private DateTime _webPartsReadAt = DateTime.MinValue;

    internal IReadOnlyList<string> WebParts => _webParts;

    internal int WebCurrentPart => _webCurrentPart;

    /// <summary>
    /// 读一遍页面里的选集列表(轮询里按间隔调用,不必每 200ms 都读)。
    /// <para>
    /// 当前项靠 B 站自己打的 <c>bpx-state-multi-active-item</c> 类判断 —— 比解析网址里的
    /// <c>?p=</c> 准:站内切换时地址栏不一定会立刻变,而高亮项是点击的直接结果。
    /// </para>
    /// </summary>
    private async Task ReadWebPartsAsync(CoreWebView2 core)
    {
        const string script = "(() => { " +
            " const items = [...document.querySelectorAll('" + PartItemSelector + "')];" +
            " if (!items.length) return null;" +
            " let current = -1;" +
            " const titles = items.map((el, i) => {" +
            "   if (el.classList.contains('bpx-state-multi-active-item')) current = i;" +
            "   return (el.textContent || '').replace(/\\s+/g, ' ').trim();" +
            " });" +
            " return { t: titles, c: current };" +
            "})();";

        try
        {
            string json = await core.ExecuteScriptAsync(script);

            string signature;

            if (string.IsNullOrEmpty(json) || json == "null")
            {
                // 这个页面没有选集列表(单P 视频、或者播放器 / 面板还没渲染出来)。
                ClearWebParts();
                signature = string.Empty;
            }
            else
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                System.Text.Json.JsonElement root = doc.RootElement;

                _webParts.Clear();

                foreach (System.Text.Json.JsonElement title in root.GetProperty("t").EnumerateArray())
                    _webParts.Add(title.GetString() ?? string.Empty);

                _webCurrentPart = root.GetProperty("c").GetInt32();
                signature = BuildWebPartsSignature();
            }

            if (signature == _webPartsSignature)
                return;

            _webPartsSignature = signature;

            // 选集列表变了(读到了、或者当前项换了):控制条那行字跟着变。
            UpdateWebLabel();
        }
        catch
        {
            // 页面正在切换 / WebView 正在重建:当读不到,下一轮再来。
        }
    }

    private string BuildWebPartsSignature()
        => _webCurrentPart + "|" + string.Join('\u0001', _webParts);

    /// <summary>把选集列表清空(换了文档 / 退出网页模式时用)。</summary>
    private void ClearWebParts()
    {
        _webParts.Clear();
        _webCurrentPart = -1;
        _webPartsSignature = string.Empty;
        _webPartsReadAt = DateTime.MinValue;
    }

    /// <summary>
    /// 切到选集列表里的第 <paramref name="index"/> 项:点页面里那一项,走站点自己的切换逻辑。
    /// <para>
    /// 这样切集是<b>站内切换</b> —— 不重新加载页面,网页全屏、进度条、播放状态都不被打断。
    /// 早先是"改网址导航"(<c>?p=N</c>),那会让整个页面重载:画面闪一下白、全屏要重新点,
    /// 体验差得很明显。
    /// </para>
    /// <para>
    /// 点击用"重新查一遍列表 + 取第 index 项"而不是记住元素:面板会被 B 站重渲染,
    /// 存下来的元素引用会失效;而选择器 + 下标每次都是现查的。
    /// </para>
    /// </summary>
    internal void WebSelectPart(int index)
    {
        if (!_webMode || index < 0 || index >= _webParts.Count)
            return;

        string script = "(() => { " +
            " const items = [...document.querySelectorAll('" + PartItemSelector + "')];" +
            " const el = items[" + index + "];" +
            " if (el) el.click();" +
            "})();";

        _ = ExecuteWebScriptAsync(script);

        // 先按"已经切过去了"记一笔,下拉栏的高亮项和控制条都立刻跟上,不用干等下一次读取(最多 1 秒)。
        _webCurrentPart = index;
        _webPartsSignature = BuildWebPartsSignature();
        UpdateWebLabel();
    }

    /// <summary>网页模式的"上一集 / 下一集":切上/下一个分P(到头的方向不做环绕,和站点一致)。</summary>
    internal void WebSelectAdjacentPart(int direction)
    {
        if (!_webMode || _webParts.Count == 0)
            return;

        int current = _webCurrentPart >= 0 ? _webCurrentPart : 0;
        int target = Math.Clamp(current + direction, 0, _webParts.Count - 1);

        if (target != current)
            WebSelectPart(target);
    }

    /// <summary>
    /// "自动网页全屏":页面加载完后,替用户点一下网站自己的「网页全屏」按钮。
    /// <para>
    /// 为什么改成"点按钮":以前是自己写 CSS 把 &lt;video&gt; 拉成
    /// <c>position:fixed; inset:0</c>,但那只动了 video 一个元素,网站自己的布局层级没变 ——
    /// 比如 B 站顶部那条白色导航栏(<c>.bili-header__bar</c>,<c>position:fixed</c>、z-index 1002)
    /// 依旧在原地,于是画面上方永远留着一条白条。点网站自己的按钮,走的是它自己的完整实现
    /// (B 站是给 <c>#bilibili-player</c> 挂上 <c>mode-webscreen</c> 类 →
    /// <c>position:fixed; inset:0; z-index:100000</c>),控制条、弹幕层会被一起抬到最上层,
    /// 不留残渣,也顺带保住了网站自己的播放控件。
    /// </para>
    /// <para>
    /// 这种"网页全屏"是纯 CSS 实现的,不是浏览器的 Fullscreen API,所以不要求"用户手势",
    /// 脚本合成的 click 就能生效。用轮询是因为播放器控件常常在导航完成之后才渲染出来
    /// (而且是"鼠标移上去才出现"的),也可能被重建。
    /// </para>
    /// </summary>
    private async Task AutoWebFullscreenAsync()
    {
        const string script = @"(() => {
    const BUTTON = '.bpx-player-ctrl-web';
    const FILL_CSS = 'position:fixed !important;inset:0 !important;width:100% !important;' +
                     'height:100% !important;z-index:2147483647 !important;' +
                     'object-fit:contain !important;background:#000 !important;';

    // 已经在网页全屏里了?看播放器容器有没有变成「铺满视口的 fixed 层」。
    // 只要这里返回 false,就说明页面还是普通形态,点一下按钮是安全的(不会反而把全屏点掉)。
    const inWebScreen = () => {
        const els = [document.querySelector('#bilibili-player'),
                     document.querySelector('.bpx-player-container')];
        for (const el of els) {
            if (!el) continue;
            if (el.classList.contains('mode-webscreen')) return true;
            if (getComputedStyle(el).position !== 'fixed') continue;
            const r = el.getBoundingClientRect();
            if (r.width >= innerWidth * 0.98 && r.height >= innerHeight * 0.98) return true;
        }
        const btn = document.querySelector(BUTTON);
        return !!btn && (btn.getAttribute('aria-label') || '').indexOf('退出') !== -1;
    };

    // 控制条是「鼠标移上去才出来」的,没动过鼠标时按钮可能压根没渲染 —— 先隔空挪一下鼠标叫醒它。
    const wake = () => {
        const area = document.querySelector('.bpx-player-video-area') ||
                     document.querySelector('#bilibili-player');
        if (!area) return;
        const r = area.getBoundingClientRect();
        if (r.width < 2 || r.height < 2) return;
        const opts = {
            bubbles: true, cancelable: true, composed: true, view: window,
            clientX: r.left + r.width / 2, clientY: r.top + r.height / 2
        };
        area.dispatchEvent(new PointerEvent('pointermove', opts));
        area.dispatchEvent(new MouseEvent('mousemove', opts));
    };

    // 兜底:页面里压根没有「网页全屏」这东西(非 B 站),才退回「自己把主视频铺满」的老办法。
    // 只认「正在播放 + 够大」的视频,免得把 B 站首页那种悬停预览小窗给拉成大屏。
    const fallbackFill = () => {
        for (const v of document.querySelectorAll('video')) {
            const r = v.getBoundingClientRect();
            if (v.paused || r.width < innerWidth * 0.6 || r.height < innerHeight * 0.4) continue;
            if (v.dataset.cheatSheetFull === '1') return;
            v.dataset.cheatSheetFull = '1';
            v.style.cssText += ';' + FILL_CSS;
            document.documentElement.style.setProperty('overflow', 'hidden', 'important');
            return;
        }
    };

    let clicks = 0;
    let clickedAt = 0;
    const startedAt = Date.now();

    const tick = () => {
        if (inWebScreen()) return true;

        // 刚点过就给它 2 秒反应时间,否则自己的重试会把刚进去的全屏又点出来。
        if (clicks > 0 && Date.now() - clickedAt < 2000) return false;

        const btn = document.querySelector(BUTTON);

        if (btn) {
            wake();
            btn.click();
            clickedAt = Date.now();
            return ++clicks >= 3;   // 最多试 3 次(奇数次);万一状态判不出来,也停在「已进入」
        }

        wake();

        // 等了 10 秒还没这个按钮,说明这个站没有「网页全屏」,走兜底。
        if (Date.now() - startedAt > 10000) {
            fallbackFill();
            return true;
        }

        return false;
    };

    if (tick()) return;
    const timer = setInterval(() => { if (tick()) clearInterval(timer); }, 600);
    setTimeout(() => clearInterval(timer), 60000);
})();";

        try
        {
            await ExecuteWebScriptAsync(script);
        }
        catch
        {
            // 自动全屏是锦上添花,失败不影响播放。
        }
    }

    /// <summary>
    /// 把用户输入的地址规整一下:
    /// <list type="bullet">
    /// <item>分享口令 / 复制粘贴的那段文字里黏着链接(如 B 站「复制此链接」给的口令):抠出里面第一个
    ///       <c>http(s)://…</c>,并去掉末尾常见标点(避免浏览器把 <c>。</c> / <c>，</c> / 右引号当成 URL 的一部分)。</item>
    /// <item>形如 <c>bilibili.com</c> 这种没写协议的,补成 <c>https://</c>。</item>
    /// <item><c>about:blank</c> 这类特殊 scheme 原样返回。</item>
    /// </list>
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ShareUrlRegex
        = new(@"https?://[^\s'""<>，。\u3001\u3002\uFF01\uFF1F]+",
              System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string NormalizeUrl(string url)
    {
        url = url.Trim();

        // 分享文本里抠链接
        var m = ShareUrlRegex.Match(url);
        if (m.Success)
        {
            string found = m.Value;

            // 把容易黏在链接尾部的中文标点 / 右半边括号 / 右引号都去掉
            char[] trim = { '。', '，', ',', '.', ')', '】', '」', '』', '"', ';', '；' };
            return found.TrimEnd(trim);
        }

        if (url.Contains("://", StringComparison.Ordinal))
            return url;

        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            return url;

        return "https://" + url;
    }

    /// <summary>
    /// 把 WebView 整个收掉:退出网页模式时调用,关闭窗口时也调用。
    /// <para>
    /// 顺序很重要:先 <c>Stop()</c> 停掉导航,再把它从可视树里<b>摘下来</b>,最后才 <c>Dispose()</c>。
    /// 还在可视树里就直接 Dispose,渲染那边(D3DImage)会留下悬空的引用。
    /// </para>
    /// <para>
    /// 摘掉之后它背后那组 Edge 进程就会退出,内存 / GPU / 网络全部还给系统
    /// (运行时为了复用会多留一小会儿,然后自己走)。下次进网页模式会 <c>new</c> 一个新的 ——
    /// <c>Dispose</c> 过的实例不能再初始化。
    /// </para>
    /// </summary>
    private void ReleaseWebView()
    {
        WebView2CompositionControl? view = WebView;

        WebView = null;
        _webInitialized = false;
        _webStatePolling = false;

        if (view is null)
            return;

        try
        {
            view.CoreWebView2?.Stop();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            WebHost.Children.Remove(view);
            view.Dispose();
        }
        catch
        {
            // 忽略:收资源失败也不该影响退出。
        }
    }
}
