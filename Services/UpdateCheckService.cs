using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Vamsync.Services;

public sealed class UpdateCheckService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/michael-b-tt0/Vamsync_Handy/releases/latest";
    private const string RepositoryUrl = "https://github.com/michael-b-tt0/Vamsync_Handy";

    private readonly HttpClient _httpClient;
    private readonly ILogger<UpdateCheckService> _logger;

    public UpdateCheckService(HttpClient httpClient, ILogger<UpdateCheckService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            request.Headers.UserAgent.ParseAdd("VamsyncHandy");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(
                    IsUpdateAvailable: false,
                    CurrentVersion: AppInfo.Current.VersionString,
                    LatestVersion: string.Empty,
                    ReleasePageUrl: RepositoryUrl,
                    DownloadUrl: RepositoryUrl,
                    Message: "No published releases found yet.");
            }

            response.EnsureSuccessStatusCode();

            var release = await response.Content.ReadFromJsonAsync<GitHubReleaseDto>(cancellationToken: cancellationToken);
            if (release is null)
            {
                return UpdateCheckResult.Failed("GitHub did not return release data.");
            }

            var currentVersionText = AppInfo.Current.VersionString;
            if (!TryParseVersion(currentVersionText, out var currentVersion))
            {
                return UpdateCheckResult.Failed($"Current app version '{currentVersionText}' could not be parsed.");
            }

            if (!TryParseVersion(release.TagName, out var latestVersion))
            {
                return UpdateCheckResult.Failed($"Release tag '{release.TagName}' could not be parsed.");
            }

            var downloadUrl = ResolveDownloadUrl(release);
            return new UpdateCheckResult(
                IsUpdateAvailable: latestVersion > currentVersion,
                CurrentVersion: currentVersion.ToString(),
                LatestVersion: latestVersion.ToString(),
                ReleasePageUrl: string.IsNullOrWhiteSpace(release.HtmlUrl) ? RepositoryUrl : release.HtmlUrl,
                DownloadUrl: downloadUrl,
                Message: latestVersion > currentVersion
                    ? $"Version {latestVersion} is available."
                    : "You are on the latest version.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check for app updates.");
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    private static string ResolveDownloadUrl(GitHubReleaseDto release)
    {
        var preferredAsset = (release.Assets ?? []).FirstOrDefault(asset =>
            asset.BrowserDownloadUrl is not null &&
            (asset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
             || asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
             || asset.Name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)
             || asset.Name.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase)));

        return preferredAsset?.BrowserDownloadUrl
            ?? release.HtmlUrl
            ?? RepositoryUrl;
    }

    private static bool TryParseVersion(string? rawVersion, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return false;
        }

        var normalized = rawVersion.Trim();
        if (normalized.StartsWith("v", true, CultureInfo.InvariantCulture))
        {
            normalized = normalized[1..];
        }

        if (!Version.TryParse(normalized, out var parsedVersion))
        {
            return false;
        }

        version = parsedVersion;
        return true;
    }

    private sealed record GitHubReleaseDto(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("assets")] IReadOnlyList<GitHubReleaseAssetDto>? Assets);

    private sealed record GitHubReleaseAssetDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);
}

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleasePageUrl,
    string DownloadUrl,
    string Message)
{
    public static UpdateCheckResult Failed(string message) =>
        new(
            IsUpdateAvailable: false,
            CurrentVersion: AppInfo.Current.VersionString,
            LatestVersion: string.Empty,
            ReleasePageUrl: "https://github.com/michael-b-tt0/Vamsync_Handy",
            DownloadUrl: "https://github.com/michael-b-tt0/Vamsync_Handy",
            Message: message);
}
