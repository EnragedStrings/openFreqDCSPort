using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Common.Signaling;

namespace OpenFreq.Common.Tests;

public class OpenFreqRtcClientTests
{
    /// <summary>
    /// A client has no RTP sender before it connects, or between a drop and the end of the reconnect.
    /// </summary>
    [Fact]
    public void MarkTransmitStartTime_WithoutAnRtpSender_DoesNothing()
    {
        using var client = new OpenFreqRtcClient(NullLoggerFactory.Instance, "127.0.0.1", "", "Tester");

        client.MarkTransmitStartTime();
    }

    /// <summary>
    /// With no connection, a queued send is dropped rather than thrown or left pending, so a caller
    /// awaiting it (or a send queued behind it) is never stuck.
    /// </summary>
    [Fact]
    public async Task SendMessageAsync_WithoutAConnection_CompletesWithoutSending()
    {
        using var client = new OpenFreqRtcClient(NullLoggerFactory.Instance, "127.0.0.1", "", "Tester");
        var errors = 0;
        client.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);

        var sends = Enumerable.Range(0, 10)
            .Select(_ => client.SendMessageAsync(SignalingMessageFactory.CreateModeUpdate(is3d: true)))
            .ToList();
        await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, errors);
    }
}
