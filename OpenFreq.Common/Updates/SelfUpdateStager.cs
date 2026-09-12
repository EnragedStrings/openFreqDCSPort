using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenFreq.Common.Updates;

/// <summary>
/// Downloads and extracts a release asset located by UpdateChecker into a local staging
/// directory, ready for SelfUpdateLauncher to swap in. Writes StagedUpdateMarker only once
/// extraction has fully succeeded, so a later process/launch can discover "an update is ready"
/// without re-deriving anything.
/// </summary>
public sealed class SelfUpdateStager
{
    public const string MarkerFileName = "staged.json";

    /// <summary>Written right before a launcher swap is armed (see SelfUpdateLauncher) --
    /// distinct from MarkerFileName ("ready to apply") because this one means "just applied,
    /// show what changed" and is consumed by the relaunched process, not the one that wrote it.
    /// </summary>
    public const string AppliedMarkerFileName = "applied.json";

    private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly ILogger _logger;
    private readonly HttpClient _http;

    public SelfUpdateStager(ILogger logger, HttpClient? httpClient = null)
    {
        _logger = logger;
        _http = httpClient ?? DefaultHttp;
    }

    /// <summary>Downloads and extracts <paramref name="info"/> into a version-named subfolder of
    /// <paramref name="stagingDir"/>, returning the path to the extracted executable
    /// (<paramref name="exeFileName"/>, e.g. "OpenFreq.Client.exe" / "OpenFreq.Server"). Returns
    /// null (logs a warning) on any failure -- a failed background download should never crash
    /// the app it's silently helping to update. <paramref name="progress"/> (0-1) is reported as
    /// bytes download; left null by callers with nothing to show it to (background/silent updates,
    /// the headless server) -- when the response has no Content-Length, it's simply never invoked,
    /// which callers treat as "stay indeterminate".</summary>
    public async Task<string?> DownloadAndStageAsync(UpdateInfo info, string stagingDir, string exeFileName,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var versionDir = Path.Combine(stagingDir, info.Version);
            Directory.CreateDirectory(versionDir);

            var zipPath = Path.Combine(stagingDir, info.AssetFileName);
            using (var response = await _http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength;

                await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
                await using var file = File.Create(zipPath);

                var buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    totalRead += read;
                    if (totalBytes is > 0)
                        progress?.Report((double)totalRead / totalBytes.Value);
                }
            }

            ZipFile.ExtractToDirectory(zipPath, versionDir, overwriteFiles: true);
            File.Delete(zipPath);

            var exePath = Path.Combine(versionDir, exeFileName);
            if (!File.Exists(exePath))
            {
                _logger.LogWarning("Staged update {Version} extracted but {ExeFileName} was not found in it",
                    info.Version, exeFileName);
                return null;
            }

            var marker = new StagedUpdateMarker
            {
                Version = info.Version,
                ReleaseNotes = info.ReleaseNotes,
                ExePath = exePath
            };
            await File.WriteAllTextAsync(Path.Combine(stagingDir, MarkerFileName),
                JsonSerializer.Serialize(marker, UpdateJsonContext.Default.StagedUpdateMarker), ct);

            return exePath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download/stage update {Version}", info.Version);
            return null;
        }
    }

    /// <summary>Reads and clears (deletes) a previously-staged "ready to apply" marker, if
    /// present. Clearing, not just reading, matters: it stops the same update from being
    /// (re-)applied forever if something goes wrong after this point.</summary>
    public static StagedUpdateMarker? TakeStagedMarker(string stagingDir) =>
        TakeMarker(Path.Combine(stagingDir, MarkerFileName));

    /// <summary>Call right before arming a SelfUpdateLauncher swap, so the relaunched process can
    /// show "here's what changed" via TakeAppliedMarker once its own window is up.</summary>
    public static void WriteAppliedMarker(string stagingDir, string version, string releaseNotes)
    {
        var marker = new StagedUpdateMarker { Version = version, ReleaseNotes = releaseNotes };
        File.WriteAllText(Path.Combine(stagingDir, AppliedMarkerFileName),
            JsonSerializer.Serialize(marker, UpdateJsonContext.Default.StagedUpdateMarker));
    }

    /// <summary>Reads and clears the "just applied" marker written by WriteAppliedMarker, if
    /// present -- the "what's new" popup's data source.</summary>
    public static StagedUpdateMarker? TakeAppliedMarker(string stagingDir) =>
        TakeMarker(Path.Combine(stagingDir, AppliedMarkerFileName));

    private static StagedUpdateMarker? TakeMarker(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), UpdateJsonContext.Default.StagedUpdateMarker);
        }
        catch
        {
            return null;
        }
        finally
        {
            File.Delete(path);
        }
    }
}
