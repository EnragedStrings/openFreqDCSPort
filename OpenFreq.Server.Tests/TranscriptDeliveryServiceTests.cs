using OpenFreq.Common;
using OpenFreqServer;

namespace OpenFreq.Server.Tests;

/// <summary>
/// Exercises TranscriptDeliveryService's pure interval-overlap and word-redaction math with exact
/// synthetic timestamps -- deliberately NOT timing-based (no real Task.Delay/DateTime.UtcNow), so
/// these are fast and can never flake on scheduling jitter. End-to-end wiring (the server actually
/// gating/relaying over a real connection) is covered separately in
/// Integration/TranscriptDeliveryIntegrationTests.cs.
/// </summary>
public class TranscriptDeliveryServiceTests
{
    private static TranscriptDeliveryService.PendingTransmission MakeTx(DateTime start, DateTime? end,
        string peerId = "peer", int freq = 251_000) =>
        new() { PeerId = peerId, FrequencyKhz = freq, StartedAtUtc = start, EndedAtUtc = end };

    [Fact]
    public void OverlapsInTime_DisjointWindows_False()
    {
        var t0 = DateTime.UtcNow;
        var a = MakeTx(t0, t0 + TimeSpan.FromSeconds(1));
        var b = MakeTx(t0 + TimeSpan.FromSeconds(2), t0 + TimeSpan.FromSeconds(3));

        Assert.False(TranscriptDeliveryService.OverlapsInTime(a, b));
        Assert.False(TranscriptDeliveryService.OverlapsInTime(b, a));
    }

    [Fact]
    public void OverlapsInTime_PartialOverlap_True()
    {
        var t0 = DateTime.UtcNow;
        var a = MakeTx(t0, t0 + TimeSpan.FromSeconds(2));
        var b = MakeTx(t0 + TimeSpan.FromSeconds(1), t0 + TimeSpan.FromSeconds(3));

        Assert.True(TranscriptDeliveryService.OverlapsInTime(a, b));
        Assert.True(TranscriptDeliveryService.OverlapsInTime(b, a));
    }

    [Fact]
    public void OverlapsInTime_OneFullyContainsOther_True()
    {
        var t0 = DateTime.UtcNow;
        var a = MakeTx(t0, t0 + TimeSpan.FromSeconds(5));
        var b = MakeTx(t0 + TimeSpan.FromSeconds(1), t0 + TimeSpan.FromSeconds(2));

        Assert.True(TranscriptDeliveryService.OverlapsInTime(a, b));
    }

    [Fact]
    public void OverlapsInTime_StillActiveTransmission_TreatedAsOpenEnded()
    {
        var t0 = DateTime.UtcNow;
        // 'a' already ended; 'b' has no end yet (still transmitting) -- 'b' should still be
        // considered overlapping since it started before 'a' ended and hasn't ended itself.
        var a = MakeTx(t0, t0 + TimeSpan.FromSeconds(1));
        var b = MakeTx(t0 + TimeSpan.FromMilliseconds(500), null);

        Assert.True(TranscriptDeliveryService.OverlapsInTime(a, b));
    }

    [Fact]
    public void OverlapsInTime_AdjacentNonOverlapping_False()
    {
        var t0 = DateTime.UtcNow;
        // b starts exactly when a ends -- touching, not overlapping.
        var a = MakeTx(t0, t0 + TimeSpan.FromSeconds(1));
        var b = MakeTx(t0 + TimeSpan.FromSeconds(1), t0 + TimeSpan.FromSeconds(2));

        Assert.False(TranscriptDeliveryService.OverlapsInTime(a, b));
    }

    private static TranscriptWordDto Word(string text, double start, double end) =>
        new() { Text = text, StartSec = start, EndSec = end };

    [Fact]
    public void BuildText_NoDropWindows_ConcatenatesAllWords()
    {
        var words = new List<TranscriptWordDto>
        {
            Word("Nellis", 0.0, 0.3), Word(" Tower,", 0.3, 0.7), Word(" request", 1.0, 1.4), Word(" taxi", 1.4, 1.7)
        };

        var text = TranscriptDeliveryService.BuildText(words, null);

        Assert.Equal("Nellis Tower, request taxi", text);
    }

    [Fact]
    public void BuildText_WordFullyInsideDropWindow_Removed()
    {
        var words = new List<TranscriptWordDto>
        {
            Word("Nellis", 0.0, 0.3), Word(" Tower,", 0.3, 0.7), Word(" request", 1.0, 1.4), Word(" taxi", 1.4, 1.7)
        };
        var dropWindows = new List<(double, double)> { (0.9, 1.3) };

        var text = TranscriptDeliveryService.BuildText(words, dropWindows);

        Assert.Equal("Nellis Tower, taxi", text);
    }

    [Fact]
    public void BuildText_WordPartiallyOverlappingDropWindow_StillRemoved()
    {
        // Word spans [0.3, 0.7); drop window is [0.6, 2.0) -- only the tail overlaps, but the
        // whole word is dropped (can't un-garble half a word from stepped audio).
        var words = new List<TranscriptWordDto> { Word("Nellis", 0.0, 0.3), Word(" Tower,", 0.3, 0.7) };
        var dropWindows = new List<(double, double)> { (0.6, 2.0) };

        var text = TranscriptDeliveryService.BuildText(words, dropWindows);

        Assert.Equal("Nellis", text);
    }

    [Fact]
    public void BuildText_MultipleDropWindows_UnionApplied()
    {
        var words = new List<TranscriptWordDto>
        {
            Word("one", 0.0, 0.4), Word(" two", 0.6, 1.0), Word(" three", 1.2, 1.5), Word(" four", 1.8, 2.2)
        };
        var dropWindows = new List<(double, double)> { (0.0, 0.5), (1.2, 1.6) };

        var text = TranscriptDeliveryService.BuildText(words, dropWindows);

        // "one" drops (window 1), "two" survives (gap between windows), "three" drops (window 2),
        // "four" survives (after window 2 ends). Overall Trim() strips the leading space from the
        // surviving " two" token since it's now first.
        Assert.Equal("two four", text);
    }

    [Fact]
    public void BuildText_EntireTranscriptDropped_ReturnsEmpty()
    {
        var words = new List<TranscriptWordDto> { Word("Nellis", 0.0, 0.3), Word(" Tower", 0.3, 0.7) };
        var dropWindows = new List<(double, double)> { (0.0, 1.0) };

        var text = TranscriptDeliveryService.BuildText(words, dropWindows);

        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void BuildText_EmptyDropWindowList_TreatedSameAsNull()
    {
        var words = new List<TranscriptWordDto> { Word("checking in", 0.0, 0.5) };

        var text = TranscriptDeliveryService.BuildText(words, new List<(double, double)>());

        Assert.Equal("checking in", text);
    }
}
