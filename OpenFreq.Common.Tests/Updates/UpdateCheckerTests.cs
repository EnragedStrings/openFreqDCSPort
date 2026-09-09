using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Common.Updates;

namespace OpenFreq.Common.Tests.Updates;

/// <summary>
/// UpdateChecker against a fake HttpMessageHandler -- no real network access, deterministic.
/// </summary>
public class UpdateCheckerTests
{
    private const string AssetWin = "OpenFreq-Client-v1.2.0-win-x64-portable.zip";

    private static UpdateChecker MakeChecker(string? responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHandler(responseBody, status);
        var client = new HttpClient(handler);
        return new UpdateChecker(NullLogger.Instance, client);
    }

    private static string ReleaseJson(string tag, string body = "notes", params string[] assetNames)
    {
        var assets = string.Join(",", assetNames.Select(n =>
            $$"""{"name":"{{n}}","browser_download_url":"https://example.com/{{n}}"}"""));
        return $$"""{"tag_name":"{{tag}}","body":"{{body}}","assets":[{{assets}}]}""";
    }

    [Fact]
    public async Task NewerVersionAvailable_ReturnsUpdateInfo()
    {
        var checker = MakeChecker(ReleaseJson("v1.2.0", "notes", AssetWin));

        var result = await checker.CheckForUpdateAsync("Client", "1.1.0");

        Assert.NotNull(result);
        Assert.Equal("1.2.0", result.Version);
        Assert.Equal("notes", result.ReleaseNotes);
        Assert.Equal(AssetWin, result.AssetFileName);
    }

    [Fact]
    public async Task SameVersion_ReturnsNull()
    {
        var checker = MakeChecker(ReleaseJson("v1.1.0", "notes", AssetWin));

        var result = await checker.CheckForUpdateAsync("Client", "1.1.0");

        Assert.Null(result);
    }

    [Fact]
    public async Task OlderReleaseThanCurrent_ReturnsNull()
    {
        var checker = MakeChecker(ReleaseJson("v1.0.0", "notes", AssetWin));

        var result = await checker.CheckForUpdateAsync("Client", "1.1.0");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("0.0.0-local")]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task UnparseableCurrentVersion_NeverChecksNetwork_ReturnsNull(string currentVersion)
    {
        // A handler that would throw if actually invoked -- proves the network call is skipped.
        var handler = new ThrowingHandler();
        var checker = new UpdateChecker(NullLogger.Instance, new HttpClient(handler));

        var result = await checker.CheckForUpdateAsync("Client", currentVersion);

        Assert.Null(result);
    }

    [Fact]
    public async Task NoMatchingAsset_ReturnsNull()
    {
        var checker = MakeChecker(ReleaseJson("v1.2.0", "notes", "OpenFreq-Server-v1.2.0-linux-x64-portable.zip"));

        var result = await checker.CheckForUpdateAsync("Client", "1.1.0");

        Assert.Null(result);
    }

    [Fact]
    public async Task HttpError_ReturnsNullWithoutThrowing()
    {
        var checker = MakeChecker(null, HttpStatusCode.InternalServerError);

        var result = await checker.CheckForUpdateAsync("Client", "1.1.0");

        Assert.Null(result);
    }

    [Fact]
    public async Task MalformedJson_ReturnsNullWithoutThrowing()
    {
        var checker = MakeChecker("{ not valid json");

        var result = await checker.CheckForUpdateAsync("Client", "1.1.0");

        Assert.Null(result);
    }

    private sealed class FakeHandler(string? body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(status);
            if (body != null)
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("Network should never be reached for an unparseable current version");
    }
}
