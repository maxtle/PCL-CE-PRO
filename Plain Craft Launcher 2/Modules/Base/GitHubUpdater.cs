using System.IO;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
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
    public const string CurrentTag = "v2.15.1-pro.4";

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
    ///     下载更新包到更新器约定的位置（<c>程序目录\PCL\PCL-CE PRO.exe</c>），
    ///     供 <c>UpdateManager.UpdateRestart</c> 启动新版本完成自替换。
    /// </summary>
    /// <param name="url">安装包下载地址。</param>
    /// <param name="expectedSize">期望大小（字节），用于下载后校验，0 表示不校验。</param>
    /// <param name="progress">下载进度回调（0–100）。</param>
    /// <param name="token">取消标记。</param>
    /// <returns>下载完成的文件路径；失败返回 null，原因写入日志。</returns>
    public static async Task<string?> DownloadUpdateAsync(
        string url,
        long expectedSize,
        IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        try
        {
            var targetDir = Path.Combine(Basics.ExecutableDirectory, "PCL");
            Directory.CreateDirectory(targetDir);
            var targetPath = Path.Combine(targetDir, "PCL-CE PRO.exe");

            // 先删除残留，避免与旧文件混合写入
            if (File.Exists(targetPath))
                File.Delete(targetPath);

            Common.Log($"[Update] 开始下载更新包：{url}");

            using var response = await HttpRequest.Create(url).SendAsync().ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // 总长度以调用方给出的期望值为准（来自 Release 资产大小），拿不到时进度按 0 处理
            var total = expectedSize;
            await using (var source = await response.AsStreamAsync().ConfigureAwait(false))
            await using (var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    received += read;
                    if (total > 0)
                        progress?.Report(received * 100d / total);
                }
            }

            var actual = new FileInfo(targetPath).Length;
            if (expectedSize > 0 && actual != expectedSize)
            {
                Common.Log($"[Update] 下载大小不符，期望 {expectedSize} 实际 {actual}，已删除");
                try
                {
                    File.Delete(targetPath);
                }
                catch
                {
                    // 忽略清理失败
                }

                return null;
            }

            Common.Log($"[Update] 更新包下载完成：{actual} 字节");
            return targetPath;
        }
        catch (Exception ex)
        {
            Common.Log(ex, "[Update] 下载更新包失败");
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
