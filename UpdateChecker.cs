using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CheatSheet;

/// <summary>
/// 检查 GitHub Releases 上有没有新版本。
/// <para>
/// 程序是"绿色版"(解压即用)的 exe,自己覆盖不了正在运行的自己,所以这里只做到
/// **发现新版本 + 给出下载页**;真正的替换由用户下完 zip 解压完成。
/// </para>
/// </summary>
internal static class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/wkrs15/CheatSheet/releases/latest";

    /// <summary>仓库的 Releases 页(查不到具体版本时的兜底链接)。</summary>
    internal const string ReleasesPage = "https://github.com/wkrs15/CheatSheet/releases";

    /// <summary>一个可用的新版本。</summary>
    internal sealed record UpdateInfo(Version Version, string Tag, string PageUrl);

    /// <summary>当前程序的版本(取程序集版本,和 csproj 里的 &lt;Version&gt; 一致)。</summary>
    internal static Version CurrentVersion { get; } =
        typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>
    /// 查最新版本。网络不通 / 被 GitHub 限流 / 返回看不懂时返回 null ——
    /// 更新检查失败了不该影响使用,也不该弹一堆错。
    /// </summary>
    internal static async Task<UpdateInfo?> FetchLatestAsync()
    {
        try
        {
            // 共用一个 HttpClient:每次 new 一个会留下 TIME_WAIT 的套接字,
            // 而且 .NET 推荐复用(见 HttpClient 的注释:它是一次性的"会话",不是请求级对象)。
            using HttpResponseMessage response = await Client.GetAsync(LatestReleaseApi);

            if (!response.IsSuccessStatusCode)
                return null;

            string json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out JsonElement tagElement)
                ? tagElement.GetString() ?? string.Empty
                : string.Empty;

            Version? version = ParseTag(tag);

            if (version is null)
                return null;

            string pageUrl = root.TryGetProperty("html_url", out JsonElement urlElement)
                ? urlElement.GetString() ?? ReleasesPage
                : ReleasesPage;

            return new UpdateInfo(version, tag, pageUrl);
        }
        catch
        {
            // 断网、代理不通、JSON 变了……都当"查不到"。
            return null;
        }
    }

    /// <summary>这个版本是不是比当前的新。</summary>
    internal static bool IsNewer(Version candidate) => candidate > CurrentVersion;

    /// <summary>
    /// 把标签解析成可比较的版本号:<c>v0.8</c> → 0.8.0.0,<c>v0.7.1</c> → 0.7.1.0。
    /// 后缀(<c>v0.8-beta</c>)直接忽略 —— 拿它当 0.8 用足够了。
    /// </summary>
    internal static Version? ParseTag(string tag)
    {
        Match match = Regex.Match(tag.Trim(), @"^[vV]?(\d+(?:\.\d+){0,3})");

        if (!match.Success)
            return null;

        string[] parts = match.Groups[1].Value.Split('.');
        var numbers = new int[4];

        for (int i = 0; i < parts.Length && i < numbers.Length; i++)
            int.TryParse(parts[i], out numbers[i]);

        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        try
        {
            // GitHub 的接口没有 User-Agent 会直接 403。
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CheatSheet-UpdateCheck");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }
        catch
        {
            // 请求头设不上就让它去碰运气,失败了不过是"查不到"。
        }

        return client;
    }
}
