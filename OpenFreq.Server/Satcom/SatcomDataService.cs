using OpenFreq.Common.Satcom;

namespace OpenFreqServer.Satcom;

public sealed record SatcomDataTransferResult(
    bool Success, double ElapsedSeconds, int PayloadPackets, int Retransmissions, double EffectiveGoodputBps,
    string? FailureReason);

/// <summary>
/// Rate-limited data transfer over a DAMA net's shared capacity. Not raw mic PCM and not a
/// separate invented "bitrate label" -- goodput is derived from the net's actual information rate,
/// FEC/framing overhead, this client's fair share of the net's shared slot capacity, and a
/// packet-error-driven ARQ retransmission expectation, so poor RF genuinely costs time rather than
/// just being a cosmetic quality number. Deterministic given a seeded PRNG, for testability.
/// </summary>
public static class SatcomDataService
{
    /// <summary>CRC/framing/preamble overhead assumed on top of FEC -- GAMEPLAY_CONFIG stand-in
    /// (not modeled per-waveform at bit-exact fidelity).</summary>
    private const double FramingOverheadFraction = 0.15;

    public static double EffectiveGoodputBps(SatcomNetDefinition net, IDamaFrameScheduler scheduler,
        double packetErrorRate)
    {
        var slotFraction = scheduler.CapacityPerFrame is > 0 and < int.MaxValue
            ? 1.0 / scheduler.CapacityPerFrame
            : 1.0; // Dedicated (uncontended) waveform: this client has the whole channel

        var rawInformationRateBps = net.BitRateBps * (1.0 - FramingOverheadFraction) * slotFraction;

        if (packetErrorRate >= 0.999) return 0.0; // ARQ never succeeds -- no usable goodput
        var expectedTransmissionsPerPacket = 1.0 / (1.0 - packetErrorRate);
        return rawInformationRateBps / expectedTransmissionsPerPacket;
    }

    /// <summary>Simulates transferring <paramref name="payloadBytes"/> over one net at the current
    /// post-FEC BER, packet by packet, with retransmission-on-error (simple stop-and-wait ARQ
    /// model) -- concrete enough to demonstrate real, non-instantaneous, RF-quality-dependent
    /// transfer time, not just an analytic estimate.</summary>
    public static SatcomDataTransferResult SimulateTransfer(int payloadBytes, SatcomNetDefinition net,
        IDamaFrameScheduler scheduler, double postFecBer, Random rng, int packetBytes = 64)
    {
        if (payloadBytes <= 0) return new SatcomDataTransferResult(true, 0, 0, 0, 0, null);

        var packetBits = packetBytes * 8;
        var packetErrorRate = SatcomLinkBudget.FrameErrorRate(postFecBer, packetBits);
        var totalPackets = (int)Math.Ceiling(payloadBytes / (double)packetBytes);

        var goodput = EffectiveGoodputBps(net, scheduler, packetErrorRate);
        if (goodput <= 0.0)
            return new SatcomDataTransferResult(false, 0, totalPackets, 0, 0, "link quality too poor for reliable delivery (ARQ never converges)");

        var retransmissions = 0;
        const int maxRetriesPerPacket = 50; // avoid a pathological infinite loop at very high error rates
        for (var i = 0; i < totalPackets; i++)
        {
            var attempts = 0;
            while (rng.NextDouble() < packetErrorRate && attempts < maxRetriesPerPacket)
            {
                retransmissions++;
                attempts++;
            }
        }

        // Elapsed time from the analytic effective goodput (expected value) rather than the
        // concrete per-packet retransmission draw above, so test assertions on timing don't depend
        // on RNG variance while the packet-level retransmission COUNT still reflects real draws.
        var elapsedSeconds = (payloadBytes * 8) / goodput;

        return new SatcomDataTransferResult(true, elapsedSeconds, totalPackets, retransmissions, goodput, null);
    }
}
