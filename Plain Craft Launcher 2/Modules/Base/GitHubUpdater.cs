using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PCL.Core.IO.Net.Http;
using PCL.Core.Utils;

namespace PCL;

/// <summary>
///     通过 GitHub Releases 检查更新。
///     PCL 原有的更新链路依赖 MirrorChyan 分发服务，本项目没有对应的服务账号，
///     因此改为直接读取自有仓库的 Latest Release。
/// </summary>
public static partial class GitHubUpdater
{
    public const string Owner = "maxtle";
    public const string Repo = "PCL-CE-PRO";

    private const string LatestReleaseApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
    private const string AllReleasesPage = $"https://github.com/{Owner}/{Repo}/releases";

    /// <summary>
    ///     当前构建对应的 Release 标签。每次发版需同步修改此处，用于判断是否已是最新。
    /// </summary>
    public const string CurrentTag = "v2.15.1-pro.3";

    /// <summary>一次检查的结果。</summary>
    /// <param name="HasUpdate">是否存在比当前更新的版本。</param>
    /// <param name="LatestTag">线上最新版本的标签。</param>
    /// <param name="HtmlUrl">该版本的发布页地址。</param>
    /// <param name="Body">发布说明。</param>
    /// <param name="DownloadUrl">首选安装包下载地址，可能为空。</param>
    /// <param name="AssetSizeMb">安装包大小（MB），未知为 null。</param>
    public sealed record UpdateInfo(
        bool HasUpdate,
        string LatestTag,
        string HtmlUrl,
        string Body,
        string? DownloadUrl,
        double? AssetSizeMb);

    /// <summary>
    ///     查询最新 Release。网络异常或解析失败时返回 null（调用方按「检查失败」处理）。
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var response = await HttpRequest.Create(LatestReleaseApi).SendAsync().ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var release = await response.AsJsonAsync<GitHubRelease>(JsonCompat.SerializerOptions)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(release?.TagName))
                return null;

            var tag = release.TagName!;
            var hasUpdate = CompareVersion(ParseTag(tag), ParseTag(CurrentTag)) > 0;

            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name is not null && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            return new UpdateInfo(
                hasUpdate,
                tag,
                string.IsNullOrWhiteSpace(release.HtmlUrl)
                    ? $"{AllReleasesPage}/tag/{tag}"
                    : release.HtmlUrl!,
                release.Body ?? "",
                asset?.BrowserDownloadUrl,
                asset is { Size: > 0 } ? asset.Size / 1024d / 1024d : null);
        }
        catch (Exception ex)
        {
            Common.Log(ex, "检查 GitHub 更新失败", Common.LogLevel.Developer);
            return null;
        }
    }

    /// <summary>
    ///     解析形如 <c>v2.15.1-pro.2</c> 的标签为可比较的四元组。
    ///     正式前缀（major/minor/patch）优先比较，其次比较 pro 序号。
    /// </summary>
    private static (int Major, int Minor, int Patch, int Pro) ParseTag(string tag)
    {
        var match = TagRegex().Match(tag ?? "");
        if (!match.Success)
            return (0, 0, 0, 0);

        return (
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value),
            match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0);
    }

    private static int CompareVersion((int Major, int Minor, int Patch, int Pro) left,
        (int Major, int Minor, int Patch, int Pro) right)
    {
        var result = left.Major.CompareTo(right.Major);
        if (result != 0) return result;
        result = left.Minor.CompareTo(right.Minor);
        if (result != 0) return result;
        result = left.Patch.CompareTo(right.Patch);
        if (result != 0) return result;
        return left.Pro.CompareTo(right.Pro);
    }

    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)(?:-pro\.(\d+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();

    #region 数据模型

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    #endregion
}
