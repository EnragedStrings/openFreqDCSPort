using System.Text.Json.Serialization;

namespace OpenFreq.Common.Updates;

/// <summary>Minimal shape of GitHub's "get the latest release" API response -- only the fields
/// UpdateChecker actually needs. See
/// https://docs.github.com/en/rest/releases/releases#get-the-latest-release.</summary>
public class GitHubReleaseResponse
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("assets")] public List<GitHubReleaseAsset> Assets { get; set; } = [];
}

public class GitHubReleaseAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = string.Empty;
}

/// <summary>Result of a successful update check: a newer release exists and its matching
/// platform/app asset (see UpdateChecker's asset-naming convention) was found in it.</summary>
public class UpdateInfo
{
    public required string Version { get; init; }
    public required string ReleaseNotes { get; init; }
    public required string DownloadUrl { get; init; }
    public required string AssetFileName { get; init; }
}

/// <summary>Written by SelfUpdateStager once a download+extract completes successfully, so a
/// later launch can discover "an update is ready to apply" without re-deriving anything -- see
/// SelfUpdateStager.TakeStagedMarker.</summary>
public class StagedUpdateMarker
{
    public string Version { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public string ExePath { get; set; } = string.Empty;
}

/// <summary>Separate from OpenFreqJsonContext (that one is specifically the OpenFreq wire
/// protocol) -- trim/AOT-safe source-gen JSON for the update feature's own DTOs, GitHub's API
/// response included.</summary>
[JsonSourceGenerationOptions]
[JsonSerializable(typeof(GitHubReleaseResponse))]
[JsonSerializable(typeof(StagedUpdateMarker))]
internal partial class UpdateJsonContext : JsonSerializerContext
{
}
