using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace CodeIsland.WpfApp.Services;

public sealed class WpfUpdateChecker
{
    private static readonly HttpClient HttpClient = new();
    private const string ReleasesApiUrl = "https://api.github.com/repos/KelseySking/CodeIsland-Windows/releases/latest";

    public async Task<WpfUpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = GetCurrentVersion();

        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
        request.Headers.UserAgent.ParseAdd($"CodeIsland/{FormatVersion(currentVersion)}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

        try
        {
            using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfUpdateCheckResult.Failed(
                    currentVersion,
                    $"GitHub 返回 {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagProperty) || tagProperty.GetString() is not { Length: > 0 } tagName)
                return WpfUpdateCheckResult.Failed(currentVersion, "最新版本响应缺少 tag_name");

            var latestVersion = ParseVersion(tagName);
            if (latestVersion == null)
                return WpfUpdateCheckResult.Failed(currentVersion, $"无法识别最新版本号：{tagName}");

            var releaseNotes = root.TryGetProperty("body", out var bodyProperty) ? bodyProperty.GetString() ?? "" : "";
            var releaseUrl = root.TryGetProperty("html_url", out var htmlUrlProperty) ? htmlUrlProperty.GetString() ?? "" : "";
            var downloadUrl = TryGetWinX64ZipAsset(root) ?? releaseUrl;

            return new WpfUpdateCheckResult(
                IsSuccess: true,
                HasUpdate: latestVersion > currentVersion,
                CurrentVersion: currentVersion,
                LatestVersion: latestVersion,
                DownloadUrl: downloadUrl,
                ReleaseUrl: releaseUrl,
                ReleaseNotes: releaseNotes,
                ErrorMessage: "");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WpfUpdateCheckResult.Failed(currentVersion, "检查更新已取消");
        }
        catch (Exception ex)
        {
            return WpfUpdateCheckResult.Failed(currentVersion, $"检查更新失败：{ex.Message}");
        }
    }

    public static Version GetCurrentVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
    }

    public static string FormatVersion(Version version)
    {
        var build = version.Build < 0 ? 0 : version.Build;
        return $"{version.Major}.{version.Minor}.{build}";
    }

    private static Version? ParseVersion(string tagName)
    {
        var versionText = tagName.Trim().TrimStart('v', 'V');
        return Version.TryParse(versionText, out var version) ? version : null;
    }

    private static string? TryGetWinX64ZipAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameProperty))
                continue;

            var name = nameProperty.GetString() ?? "";
            if (!name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                continue;

            if (asset.TryGetProperty("browser_download_url", out var urlProperty))
                return urlProperty.GetString();
        }

        return null;
    }
}

public sealed record WpfUpdateCheckResult(
    bool IsSuccess,
    bool HasUpdate,
    Version CurrentVersion,
    Version? LatestVersion,
    string DownloadUrl,
    string ReleaseUrl,
    string ReleaseNotes,
    string ErrorMessage)
{
    public static WpfUpdateCheckResult Failed(Version currentVersion, string errorMessage) =>
        new(false, false, currentVersion, null, "", "", "", errorMessage);
}
