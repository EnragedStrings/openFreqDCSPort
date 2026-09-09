using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Common.Updates;

namespace OpenFreq.Common.Tests.Updates;

/// <summary>
/// SelfUpdateStager's download+extract+marker lifecycle, against a fake HttpMessageHandler
/// serving an in-memory zip and a real (test-isolated, cleaned-up) temp directory for staging.
/// </summary>
public class SelfUpdateStagerTests : IDisposable
{
    private readonly string _stagingDir = Path.Combine(Path.GetTempPath(), "openfreq-update-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_stagingDir))
            Directory.Delete(_stagingDir, recursive: true);
    }

    private static byte[] MakeZip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }
        return ms.ToArray();
    }

    private static UpdateInfo MakeInfo(string version = "1.2.0") => new()
    {
        Version = version,
        ReleaseNotes = "Fixed the thing",
        DownloadUrl = "https://example.com/OpenFreq-Client-v1.2.0-win-x64-portable.zip",
        AssetFileName = "OpenFreq-Client-v1.2.0-win-x64-portable.zip"
    };

    private sealed class ZipHandler(byte[] zipBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(zipBytes)
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task SuccessfulDownload_ExtractsAndWritesMarker()
    {
        var zip = MakeZip(("OpenFreq.Client.exe", "fake exe bytes"), ("readme.txt", "hi"));
        var stager = new SelfUpdateStager(NullLogger.Instance, new HttpClient(new ZipHandler(zip)));

        var exePath = await stager.DownloadAndStageAsync(MakeInfo(), _stagingDir, "OpenFreq.Client.exe");

        Assert.NotNull(exePath);
        Assert.True(File.Exists(exePath));
        Assert.Equal("fake exe bytes", await File.ReadAllTextAsync(exePath));

        var marker = SelfUpdateStager.TakeStagedMarker(_stagingDir);
        Assert.NotNull(marker);
        Assert.Equal("1.2.0", marker.Version);
        Assert.Equal("Fixed the thing", marker.ReleaseNotes);
        Assert.Equal(exePath, marker.ExePath);
    }

    [Fact]
    public async Task MissingExeInZip_ReturnsNullAndWritesNoMarker()
    {
        var zip = MakeZip(("some-other-file.txt", "not the exe"));
        var stager = new SelfUpdateStager(NullLogger.Instance, new HttpClient(new ZipHandler(zip)));

        var exePath = await stager.DownloadAndStageAsync(MakeInfo(), _stagingDir, "OpenFreq.Client.exe");

        Assert.Null(exePath);
        Assert.Null(SelfUpdateStager.TakeStagedMarker(_stagingDir));
    }

    [Fact]
    public void TakeStagedMarker_NoMarkerPresent_ReturnsNull()
    {
        Directory.CreateDirectory(_stagingDir);

        Assert.Null(SelfUpdateStager.TakeStagedMarker(_stagingDir));
    }

    [Fact]
    public async Task TakeStagedMarker_ClearsMarkerAfterReading()
    {
        var zip = MakeZip(("OpenFreq.Client.exe", "fake"));
        var stager = new SelfUpdateStager(NullLogger.Instance, new HttpClient(new ZipHandler(zip)));
        await stager.DownloadAndStageAsync(MakeInfo(), _stagingDir, "OpenFreq.Client.exe");

        var first = SelfUpdateStager.TakeStagedMarker(_stagingDir);
        var second = SelfUpdateStager.TakeStagedMarker(_stagingDir);

        Assert.NotNull(first);
        Assert.Null(second);
    }
}
