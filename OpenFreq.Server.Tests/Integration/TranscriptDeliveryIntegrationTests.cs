using OpenFreq.Common;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// End-to-end transcript delivery/gating/stepping tests, driven by the real production client
/// over real WebSockets against a real Kestrel-hosted SignalingServer -- same harness style as
/// SignalingIntegrationTests. Precise interval-overlap/redaction math is covered separately and
/// non-flakily in TranscriptDeliveryServiceTests; these confirm the wiring itself (capability
/// gating, position gating, and stepping) actually works over a real connection.
/// </summary>
public class TranscriptDeliveryIntegrationTests
{
    private const int Freq = 251_000;

    private static List<TranscriptWordDto> Words(params (string Text, double Start, double End)[] words) =>
        words.Select(w => new TranscriptWordDto { Text = w.Text, StartSec = w.Start, EndSec = w.End }).ToList();

    [Fact]
    public async Task Transcript_DeliveredToBot_WhenTransmitterHasNoPosition()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var bot = RtcClientHarness.Create(server, "Bot", wantsTranscripts: true);
        await using var pilot = RtcClientHarness.Create(server, "Viper1");

        await bot.ConnectAsync();
        await pilot.ConnectAsync();

        // Bot declares a listening position; the pilot transmits with no position at all -- can't
        // be gated, so the bot should get the full transcript regardless.
        await bot.Client.JoinFrequencyAsync(Freq, lat: 36.0, lon: -115.0, alt: 580.0);
        await pilot.Client.JoinFrequencyAsync(Freq);

        var transmissionId = await pilot.Client.StartTransmissionAsync(Freq);
        await pilot.Client.SendTranscriptAsync(transmissionId, Words(("Nellis Tower, Viper 1", 0.0, 1.0)));
        await pilot.Client.StopTransmissionAsync(Freq);

        var delivery = await bot.TranscriptReceived.WaitForAsync();
        Assert.Equal("Nellis Tower, Viper 1", delivery.Message.Text);
        Assert.Equal(Freq, delivery.Message.FrequencyKhz);
        Assert.Equal("Viper1", delivery.Message.FromDisplayName);
        Assert.Equal(pilot.PeerId, delivery.Message.FromPeerId);
    }

    [Fact]
    public async Task Transcript_NotDeliveredToListener_WithoutWantsTranscripts()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var normalListener = RtcClientHarness.Create(server, "Eagle1"); // wantsTranscripts: false
        await using var pilot = RtcClientHarness.Create(server, "Viper1");

        await normalListener.ConnectAsync();
        await pilot.ConnectAsync();

        await normalListener.Client.JoinFrequencyAsync(Freq, lat: 36.0, lon: -115.0, alt: 580.0);
        await pilot.Client.JoinFrequencyAsync(Freq);

        var transmissionId = await pilot.Client.StartTransmissionAsync(Freq);
        await pilot.Client.SendTranscriptAsync(transmissionId, Words(("checking in", 0.0, 0.5)));
        await pilot.Client.StopTransmissionAsync(Freq);

        await normalListener.TranscriptReceived.AssertNoneAsync();
    }

    [Fact]
    public async Task Transcript_NotDelivered_WhenBothPositionsKnownButLosUnresolved()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var bot = RtcClientHarness.Create(server, "Bot", wantsTranscripts: true);
        await using var pilot = RtcClientHarness.Create(server, "Viper1");

        await bot.ConnectAsync();
        await pilot.ConnectAsync();

        await bot.Client.JoinFrequencyAsync(Freq, lat: 36.0, lon: -115.0, alt: 580.0);
        await pilot.Client.JoinFrequencyAsync(Freq);

        // Transmitter DOES report a position this time -- both sides are known, so gating applies.
        // No DCS-presence client is connected to referee, so the oracle can never resolve, and the
        // chosen fail-closed default means no delivery.
        var transmissionId = await pilot.Client.StartTransmissionAsync(Freq, lat: 34.0, lon: -116.0, alt: 3000.0);
        await pilot.Client.SendTranscriptAsync(transmissionId, Words(("checking in", 0.0, 0.5)));
        await pilot.Client.StopTransmissionAsync(Freq);

        await bot.TranscriptReceived.AssertNoneAsync();
    }

    [Fact]
    public async Task Transcript_DeliveredFull_WhenBotHasNoDeclaredPosition()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var bot = RtcClientHarness.Create(server, "Bot", wantsTranscripts: true);
        await using var pilot = RtcClientHarness.Create(server, "Viper1");

        await bot.ConnectAsync();
        await pilot.ConnectAsync();

        // Bot never declares a position -- nothing to gate against, even though the transmitter
        // does report one.
        await bot.Client.JoinFrequencyAsync(Freq);
        await pilot.Client.JoinFrequencyAsync(Freq);

        var transmissionId = await pilot.Client.StartTransmissionAsync(Freq, lat: 34.0, lon: -116.0, alt: 3000.0);
        await pilot.Client.SendTranscriptAsync(transmissionId, Words(("checking in", 0.0, 0.5)));
        await pilot.Client.StopTransmissionAsync(Freq);

        var delivery = await bot.TranscriptReceived.WaitForAsync();
        Assert.Equal("checking in", delivery.Message.Text);
    }

    [Fact]
    public async Task Transcript_TrimmedForStepping_WhenTwoTransmittersOverlap()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var bot = RtcClientHarness.Create(server, "Bot", wantsTranscripts: true);
        await using var pilotA = RtcClientHarness.Create(server, "Viper1");
        await using var pilotB = RtcClientHarness.Create(server, "Viper2");

        await bot.ConnectAsync();
        await pilotA.ConnectAsync();
        await pilotB.ConnectAsync();

        // Bot declares a position; neither pilot reports one, so the bot's audibility to each of
        // them individually can't be gated (permissive) -- isolating the test to stepping/overlap
        // behavior specifically, independent of the LOS-oracle wiring already covered above.
        await bot.Client.JoinFrequencyAsync(Freq, lat: 36.0, lon: -115.0, alt: 580.0);
        await pilotA.Client.JoinFrequencyAsync(Freq);
        await pilotB.Client.JoinFrequencyAsync(Freq);

        // Real wall-clock sequencing: A starts, B keys up while A is still talking, A releases
        // first (while B is still talking), then B releases. Generous gaps/margins so ordinary
        // scheduling jitter can't move a word across a boundary.
        var transmissionIdA = await pilotA.Client.StartTransmissionAsync(Freq);
        await Task.Delay(500);
        var transmissionIdB = await pilotB.Client.StartTransmissionAsync(Freq);
        await Task.Delay(500);
        await pilotA.Client.StopTransmissionAsync(Freq); // A's active window is ~[0, 1.0]s; overlap starts ~0.5s in
        await Task.Delay(200);
        await pilotB.Client.StopTransmissionAsync(Freq);

        // A's own words, relative to A's own start: "Nellis" and " Tower" land safely before the
        // ~0.5s overlap start; " requesting" lands safely inside A's real [0.5, 1.0]s overlap tail.
        await pilotA.Client.SendTranscriptAsync(transmissionIdA,
            Words(("Nellis", 0.0, 0.15), (" Tower,", 0.15, 0.3), (" requesting", 0.7, 0.9)));
        await pilotB.Client.SendTranscriptAsync(transmissionIdB, Words(("Viper 2, standby", 0.0, 0.4)));

        var deliveryA = await bot.TranscriptReceived.WaitForAsync(d => d.Message.FromPeerId == pilotA.PeerId);
        Assert.Equal("Nellis Tower,", deliveryA.Message.Text);
    }

    [Fact]
    public async Task Transcript_NotTrimmed_WhenTransmissionsDoNotOverlap()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var bot = RtcClientHarness.Create(server, "Bot", wantsTranscripts: true);
        await using var pilotA = RtcClientHarness.Create(server, "Viper1");
        await using var pilotB = RtcClientHarness.Create(server, "Viper2");

        await bot.ConnectAsync();
        await pilotA.ConnectAsync();
        await pilotB.ConnectAsync();

        await bot.Client.JoinFrequencyAsync(Freq, lat: 36.0, lon: -115.0, alt: 580.0);
        await pilotA.Client.JoinFrequencyAsync(Freq);
        await pilotB.Client.JoinFrequencyAsync(Freq);

        var transmissionIdA = await pilotA.Client.StartTransmissionAsync(Freq);
        await Task.Delay(200);
        await pilotA.Client.StopTransmissionAsync(Freq);
        await pilotA.Client.SendTranscriptAsync(transmissionIdA, Words(("Nellis Tower, Viper 1", 0.0, 0.5)));

        // B transmits well after A has already finished -- no overlap at all.
        await Task.Delay(500);
        var transmissionIdB = await pilotB.Client.StartTransmissionAsync(Freq);
        await Task.Delay(200);
        await pilotB.Client.StopTransmissionAsync(Freq);
        await pilotB.Client.SendTranscriptAsync(transmissionIdB, Words(("Viper 2, checking in", 0.0, 0.5)));

        var deliveryA = await bot.TranscriptReceived.WaitForAsync(d => d.Message.FromPeerId == pilotA.PeerId);
        var deliveryB = await bot.TranscriptReceived.WaitForAsync(d => d.Message.FromPeerId == pilotB.PeerId);

        Assert.Equal("Nellis Tower, Viper 1", deliveryA.Message.Text);
        Assert.Equal("Viper 2, checking in", deliveryB.Message.Text);
    }
}
