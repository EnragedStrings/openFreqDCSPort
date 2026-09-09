using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Semver;

namespace OpenFreq.Common.Updates;

/// <summary>
/// Checks the project's public GitHub Releases for a newer version than the one currently
/// running. Shared by OpenFreq.Client and OpenFreq.Server -- see SelfUpdateStager/
/// SelfUpdateLauncher for what happens once a newer version is found.
/// </summary>
public sealed class UpdateChecker
{
    private const string RepoOwner = "EnragedStrings";
    private const string RepoName = "openFreqDCSPort";

    private static readonly Uri LatestReleaseUri =
        new($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");

    // One shared, never-disposed instance per standard HttpClient guidance for production use --
    // this is a rare, occasional call (once per launch, or every few hours for a long-running
    // server), not a hot path, so a single static client is simplest and avoids socket
    // exhaustion from creating one per check. Tests substitute their own HttpClient (built on a
    // fake HttpMessageHandler) via the constructor instead.
    private static readonly HttpClient DefaultHttp = CreateDefaultClient();

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        ConfigureHeaders(client);
        return client;
    }

    private static void ConfigureHeaders(HttpClient client)
    {
        // GitHub's REST API rejects requests with no User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenFreq-Updater", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    private readonly ILogger _logger;
    private readonly HttpClient _http;

    public UpdateChecker(ILogger logger, HttpClient? httpClient = null)
    {
        _logger = logger;
        _http = httpClient ?? DefaultHttp;
        if (httpClient != null) ConfigureHeaders(httpClient);
    }

    /// <summary>Checks for a newer release. Returns null when: <paramref name="currentVersion"/>
    /// isn't a parseable release version (local dev builds report "0.0.0-local"/"unknown" --
    /// there's nothing meaningful to update from), the latest release isn't newer, the check
    /// failed (network/API error -- fails safe, never throws), or the release has no matching
    /// asset for this app/platform (a malformed/still-uploading release).
    /// <paramref name="appName"/> is "Client" or "Server", matching release.yml's asset naming.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(string appName, string currentVersion,
        CancellationToken ct = default)
    {
        if (!SemVersion.TryParse(currentVersion, SemVersionStyles.Strict, out var current))
            return null;

        GitHubReleaseResponse? release;
        try
        {
            using var response = await _http.GetAsync(LatestReleaseUri, ct);
            response.EnsureSuccessStatusCode();
            release = await response.Content.ReadFromJsonAsync(UpdateJsonContext.Default.GitHubReleaseResponse, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update check failed (network/GitHub API error) -- treating as no update available");
            return null;
        }

        if (release == null) return null;

        var tag = release.TagName.TrimStart('v', 'V');
        if (!SemVersion.TryParse(tag, SemVersionStyles.Strict, out var latest))
            return null;

        if (latest.ComparePrecedenceTo(current) <= 0)
            return null;

        var rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64";
        var assetName = $"OpenFreq-{appName}-{release.TagName}-{rid}-portable.zip";
        var asset = release.Assets.FirstOrDefault(a => a.Name == assetName);
        if (asset == null)
        {
            _logger.LogWarning("Update {Version} found but no matching asset {AssetName} in the release",
                tag, assetName);
            return null;
        }

        return new UpdateInfo
        {
            Version = tag,
            ReleaseNotes = release.Body ?? string.Empty,
            DownloadUrl = asset.BrowserDownloadUrl,
            AssetFileName = asset.Name
        };
    }
}
