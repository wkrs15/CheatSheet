using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using HandyControl.Data;
using Growl = HandyControl.Controls.Growl;

namespace CheatSheet;

/// <summary>
/// 图片 / Markdown / 文本的查看 —— 「置顶小抄」的那一半。
/// <para>
/// 攻略其实是图文比视频多:截图、笔记、PDF 都直接拖进小窗看,比"开个浏览器再找文件"顺手。
/// PDF 不在这里处理:它交给内置浏览器(Edge 自带查看器,能滚动、能缩放、能搜索),
/// 见 <see cref="MainWindow.OpenPdf"/>。
/// </para>
/// </summary>
public partial class MainWindow
{
    internal enum ViewerKind
    {
        None,
        Image,
        Text
    }

    private static readonly string[] ImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff"
    };

    /// <summary>当文本看的扩展名(Markdown 走轻量渲染,其它按等宽纯文本显示)。</summary>
    private static readonly string[] TextExtensions =
    {
        ".md", ".markdown", ".txt", ".log", ".ini", ".csv", ".yml", ".yaml", ".json"
    };

    private ViewerKind _viewerKind = ViewerKind.None;
    private readonly List<string> _viewerFiles = new();
    private int _viewerIndex = -1;

    internal bool IsViewerMode => _viewerKind != ViewerKind.None;

    internal static bool IsImageFile(string path)
        => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    internal static bool IsTextFile(string path)
        => TextExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    internal static bool IsPdfFile(string path)
        => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>这个文件小窗能不能看(视频 / 图片 / 文本 / PDF)。拖放时的分流也用它。</summary>
    internal static bool IsSupportedFile(string path)
        => IsVideoFile(path) || IsImageFile(path) || IsTextFile(path) || IsPdfFile(path);

    /// <summary>
    /// 打开一批图片 / 文本。混选时只留和第一个文件同类的那批(图片和笔记分开看更顺)。
    /// </summary>
    internal void OpenViewer(IReadOnlyList<string> paths, int startIndex = 0)
    {
        bool wantImage = paths.Count > 0 && IsImageFile(paths[0]);

        List<string> files = paths
            .Where(File.Exists)
            .Where(path => wantImage ? IsImageFile(path) : IsTextFile(path))
            .ToList();

        if (files.Count == 0)
            return;

        if (_webMode)
            ExitWebMode();

        // 视频那套先停掉:Player.Source 不清掉的话,"有没有打开东西"的判断会串
        // (暂停压暗会把图片/笔记也压暗)。
        StopLocalVideoForViewer();

        _viewerFiles.Clear();
        _viewerFiles.AddRange(files);
        _viewerIndex = -1;
        _viewerKind = wantImage ? ViewerKind.Image : ViewerKind.Text;

        ShowViewerAt(startIndex);
    }

    /// <summary>
    /// 用内置浏览器打开本地 PDF。
    /// <para>
    /// Edge 自带 PDF 查看器(滚动、缩放、搜索都有),比自己渲染省事得多 ——
    /// 之前考虑过 <c>Windows.Data.Pdf</c>,但它只给"位图"、翻页缩放全得自己写。
    /// </para>
    /// </summary>
    internal void OpenPdf(string path)
    {
        if (!File.Exists(path))
            return;

        EnterWebMode(new Uri(path).AbsoluteUri);
    }

    /// <summary>切到查看列表里的第几个(支持环绕,和播放列表一个手感)。</summary>
    internal void ShowViewerAt(int index)
    {
        if (_viewerKind == ViewerKind.None || _viewerFiles.Count == 0)
            return;

        _viewerFiles.RemoveAll(path => !File.Exists(path));

        if (_viewerFiles.Count == 0)
        {
            ExitViewer();
            return;
        }

        _viewerIndex = ((index % _viewerFiles.Count) + _viewerFiles.Count) % _viewerFiles.Count;
        string path = _viewerFiles[_viewerIndex];

        ViewerHost.Visibility = Visibility.Visible;
        HintText.Visibility = Visibility.Collapsed;
        BottomBar.Visibility = Visibility.Collapsed;

        if (_viewerKind == ViewerKind.Image)
            ShowImage(path);
        else
            ShowText(path);

        Title = $"{Path.GetFileName(path)} - CheatSheet";

        // 进度条这一套在查看模式下是空的,顺手刷成"--:--"。
        RefreshProgress();

        Growl.Info($"{(IsImageFile(path) ? "图片" : "笔记")} {_viewerIndex + 1}/{_viewerFiles.Count}:{Path.GetFileName(path)}",
            "viewer");

        RaiseStateChanged();
    }

    /// <summary>网页模式 / 本地视频要用画面了:把查看区让出来。</summary>
    private void ExitViewer()
    {
        if (_viewerKind == ViewerKind.None)
            return;

        _viewerKind = ViewerKind.None;
        _viewerFiles.Clear();
        _viewerIndex = -1;

        ViewerImage.Source = null;
        ViewerText.Document = new FlowDocument();

        ApplyViewerVisibility();
        RaiseStateChanged();
    }

    /// <summary>查看区的显示与收起,和网页模式的可见性各管各的(都靠调用方保证互斥)。</summary>
    private void ApplyViewerVisibility()
    {
        bool show = IsViewerMode;

        ViewerHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (!show)
        {
            ViewerImage.Visibility = Visibility.Collapsed;
            ViewerText.Visibility = Visibility.Collapsed;
        }

        // 查看图片 / 笔记时没有时间轴,进度条那一条收起来(和网页模式没视频时一样)。
        BottomBar.Visibility = show ? Visibility.Collapsed : Visibility.Visible;

        HintText.Visibility = show || Player.Source is not null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>进查看模式前把本地视频停干净(Source 置空,否则会一直被当成"暂停中")。</summary>
    private void StopLocalVideoForViewer()
    {
        try
        {
            _stoppingSelf = true;

            try
            {
                Player.Stop();
                Player.Source = null;
            }
            finally
            {
                _stoppingSelf = false;
            }
        }
        catch
        {
            // 释放媒体失败不影响看图。
        }

        _isPlaying = false;
        _currentMediaPath = null;
        _timer.Stop();
    }

    // ---------------- 图片 ----------------

    private void ShowImage(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();

            // OnLoad:读完就撒手,不然文件一直被锁着,用户改图/删图都会被挡。
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();

            ViewerImage.Source = bitmap;
            ViewerImage.Visibility = Visibility.Visible;
            ViewerText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Growl.Error(new GrowlInfo { Message = $"打不开这个图片: {ex.Message}", WaitTime = 4 });
        }
    }

    // ---------------- 文本 / Markdown ----------------

    private void ShowText(string path)
    {
        try
        {
            string text = ReadTextFile(path);

            ViewerText.Document = BuildDocument(text, path);
            ViewerText.Visibility = Visibility.Visible;
            ViewerImage.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Growl.Error(new GrowlInfo { Message = $"打不开这个文件: {ex.Message}", WaitTime = 4 });
        }
    }

    /// <summary>
    /// 读文本文件。先按 UTF-8 解,解不动(GBK 老文本)就交给系统的本地编码 ——
    /// 直接用 File.ReadAllText 会把国内 Windows 上的 ANSI txt 读成一片乱码,
    /// 而为了这个引一个 CodePages 包不值得,直接问系统要。
    /// </summary>
    private static string ReadTextFile(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);

        try
        {
            // 严格的 UTF-8:遇到不合法的字节就抛,能挡住"其实是 GBK"的文件。
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(StripBom(bytes));
        }
        catch (DecoderFallbackException)
        {
            return DecodeWithSystemCodePage(bytes);
        }
    }

    private static byte[] StripBom(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int MultiByteToWideChar(
        uint codePage, uint dwFlags, byte[] lpMultiByteStr, int cbMultiByte, char[] lpWideCharStr, int cchWideChar);

    /// <summary>CP_ACP = 当前系统 ANSI 代码页(简中就是 GBK)。</summary>
    private const uint CpAcp = 0;

    private static string DecodeWithSystemCodePage(byte[] bytes)
    {
        try
        {
            int length = MultiByteToWideChar(CpAcp, 0, bytes, bytes.Length, null!, 0);

            if (length <= 0)
                return Encoding.UTF8.GetString(bytes);

            var buffer = new char[length];
            MultiByteToWideChar(CpAcp, 0, bytes, bytes.Length, buffer, length);

            return new string(buffer);
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>
    /// Markdown → FlowDocument 的极简渲染:标题、列表、引用、代码块、分隔线,
    /// 行内 **加粗** / `代码` / [链接](url)。
    /// <para>
    /// 目标只是"把攻略笔记看清楚",不追求规范全覆盖;
    /// 遇到表格这种复杂结构就原样按等宽行显示 —— 能对上齐就够了。
    /// </para>
    /// </summary>
    private static FlowDocument BuildDocument(string text, string path)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 13,
            LineHeight = 21,
            Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xED))
        };

        bool plain = !string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(Path.GetExtension(path), ".markdown", StringComparison.OrdinalIgnoreCase);

        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool inCode = false;
        var fence = new StringBuilder();

        foreach (string line in lines)
        {
            // 代码块:fence 里的内容原样按等宽显示,不做任何 Markdown 解析。
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    document.Blocks.Add(CodeParagraph(fence.ToString()));
                    fence.Clear();
                }

                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                fence.AppendLine(line);
                continue;
            }

            string trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                document.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, 8) });
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                document.Blocks.Add(new BlockUIContainer(new System.Windows.Controls.Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 6, 0, 12),
                    Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))
                }));
                continue;
            }

            string heading = trimmed.TrimStart('#');
            int level = trimmed.Length - heading.Length;

            if (level is > 0 and <= 6 && heading.StartsWith(' '))
            {
                var paragraph = new Paragraph
                {
                    FontSize = level switch { 1 => 22, 2 => 18, 3 => 16, _ => 14.5 },
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, level <= 2 ? 8 : 4, 0, 8)
                };

                AppendInline(paragraph, heading.Trim(), plain);
                document.Blocks.Add(paragraph);
                continue;
            }

            // 表格 / 复杂行:原样等宽(对不齐总比乱掉强)。
            if (trimmed.StartsWith('|'))
            {
                document.Blocks.Add(CodeParagraph(line));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                var quote = new Paragraph
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(10, 2, 0, 2),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x2E, 0x90, 0xFF))
                };

                AppendInline(quote, trimmed.TrimStart('>', ' '), plain);
                document.Blocks.Add(quote);
                continue;
            }

            string bullet = BulletPrefix(trimmed, out string body);

            if (bullet.Length > 0)
            {
                var item = new Paragraph
                {
                    Margin = new Thickness(14, 0, 0, 6),
                    TextIndent = -12
                };

                item.Inlines.Add(new Run(bullet));
                AppendInline(item, body, plain);
                document.Blocks.Add(item);
                continue;
            }

            var paragraph2 = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            AppendInline(paragraph2, trimmed, plain);
            document.Blocks.Add(paragraph2);
        }

        if (inCode && fence.Length > 0)
            document.Blocks.Add(CodeParagraph(fence.ToString()));

        return document;
    }

    private static Paragraph CodeParagraph(string text) => new(new Run(text.TrimEnd()))
    {
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12.5,
        Margin = new Thickness(0, 2, 0, 10),
        Padding = new Thickness(10, 6, 10, 6),
        Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
        LineHeight = 18
    };

    /// <summary>列表前缀(无序 → •,有序 → 保留 N.)。</summary>
    private static string BulletPrefix(string line, out string body)
    {
        if (line.Length > 2 && (line[0] == '-' || line[0] == '*' || line[0] == '+') && line[1] == ' ')
        {
            body = line[2..].Trim();
            return "• ";
        }

        int digits = 0;

        while (digits < line.Length && char.IsDigit(line[digits]))
            digits++;

        if (digits > 0 && digits + 1 < line.Length && (line[digits] == '.' || line[digits] == ')') && line[digits + 1] == ' ')
        {
            body = line[(digits + 2)..].Trim();
            return line[..digits] + ". ";
        }

        body = line;
        return string.Empty;
    }

    /// <summary>行内格式:**加粗**、`代码`、[文字](链接),其余按普通文字。</summary>
    private static void AppendInline(Paragraph paragraph, string text, bool plain)
    {
        if (plain)
        {
            paragraph.Inlines.Add(new Run(text));
            return;
        }

        int index = 0;

        while (index < text.Length)
        {
            int bold = text.IndexOf("**", index, StringComparison.Ordinal);
            int code = text.IndexOf('`', index);
            int link = text.IndexOf('[', index);

            int next = FirstPositive(bold, code, link);

            if (next < 0)
            {
                paragraph.Inlines.Add(new Run(text[index..]));
                return;
            }

            if (next > index)
                paragraph.Inlines.Add(new Run(text[index..next]));

            if (next == code)
            {
                int end = text.IndexOf('`', next + 1);
                end = end < 0 ? text.Length : end;

                paragraph.Inlines.Add(new Run(text[(next + 1)..end])
                {
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xC0, 0x80))
                });

                index = end + 1;
            }
            else if (next == bold)
            {
                int end = text.IndexOf("**", next + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end;

                paragraph.Inlines.Add(new Run(text[(next + 2)..end]) { FontWeight = FontWeights.SemiBold });
                index = end + 2;
            }
            else
            {
                int close = text.IndexOf(']', next + 1);
                int open = close > 0 ? text.IndexOf('(', close) : -1;
                int end = open > 0 ? text.IndexOf(')', open) : -1;

                if (close < 0 || open < 0 || end < 0)
                {
                    paragraph.Inlines.Add(new Run(text[next].ToString()));
                    index = next + 1;
                    continue;
                }

                string label = text[(next + 1)..close];
                string url = text[(open + 1)..end];

                var hyperlink = new Hyperlink(new Run(label))
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0xB6, 0xFF)),
                    ToolTip = url
                };

                hyperlink.RequestNavigate += OnHyperlinkNavigate;
                paragraph.Inlines.Add(hyperlink);

                index = end + 1;
            }
        }
    }

    private static int FirstPositive(params int[] values)
    {
        int best = -1;

        foreach (int value in values)
        {
            if (value >= 0 && (best < 0 || value < best))
                best = value;
        }

        return best;
    }

    private static void OnHyperlinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenInBrowser(e.Uri?.AbsoluteUri ?? string.Empty);
        e.Handled = true;
    }
}
