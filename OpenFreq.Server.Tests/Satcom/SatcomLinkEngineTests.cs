using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Common.Satcom;
using OpenFreqServer.Satcom;

namespace OpenFreqServer.Tests.Satcom;

/// <summary>
/// Covers SatcomLinkEngine.Evaluate's per-net frequency source: a Dedicated (half-duplex,
/// channels 26-30 on the real ARC-210) net uses each terminal's own reported
/// SatcomTerminalState.TunedFrequencyHz for both legs instead of the net's fixed
/// UplinkHz/DownlinkHz, while a DAMA net always uses the net's fixed pair regardless of what a
/// terminal reports.
/// </summary>
public class SatcomLinkEngineTests
{
    private static readonly SatcomSatelliteDefinition Satellite = new()
    {
        Id = "test-sat", DisplayName = "Test", EphemerisMode = SatcomEphemerisMode.StaticGeo,
        StaticLongitudeDeg = 0.0
    };

    private static SatcomEphemerisService BuildEphemeris()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openfreq-satcom-linkengine-test-" + Guid.NewGuid());
        var service = new SatcomEphemerisService([Satellite], new SatcomServerConfig { EphemerisCacheDirectory = dir },
            NullLogger.Instance);
        service.PropagateAll();
        return service;
    }

    private static SatcomLinkEngine BuildEngine(out SatcomEphemerisService ephemeris)
    {
        ephemeris = BuildEphemeris();
        return new SatcomLinkEngine([Satellite], ephemeris, new SatcomSatelliteSelector());
    }

    [Fact]
    public void DedicatedNetUsesTheTerminalsOwnTunedFrequencyForBothLegs()
    {
        var engine = BuildEngine(out _);
        var net = new SatcomNetDefinition
        {
            NetId = "dedicated", DisplayName = "dedicated", Waveform = SatcomWaveform.Dedicated5k,
            SelectionMode = SatelliteSelectionMode.AutoBestVisible,
            // Deliberately absurd fixed pair -- if this were used instead of TunedFrequencyHz the
            // combined result below would be wildly different (and likely unusable).
            UplinkHz = 10_000_000_000.0, DownlinkHz = 10_000_000_000.0
        };

        const double tunedHz = 260_000_000.0;
        var terminal = new SatcomTerminalState(0.0, 0.0, 3000.0, 0.0, 0.0, 0.0, false, TunedFrequencyHz: tunedHz);

        engine.UpdateGeometry("client-a", net.NetId, terminal, radioPowered: true, pttPressed: true,
            terrainLosClear: true, nowMs: 0);

        var result = engine.Evaluate("client-a", net.NetId, net, nowMs: 0);

        Assert.True(result.LinkAvailable);
        // Cross-check against directly evaluating both legs at the tuned frequency.
        var satPos = new SatcomSatellitePosition(Satellite.Id, 0.0, 0.0, Satellite.StaticAltitudeMeters, 0, false);
        var expectedUp = SatcomLinkEngineCore.EvaluateLeg(net, Satellite, satPos, terminal, tunedHz, isUplinkLeg: true);
        var expectedDown = SatcomLinkEngineCore.EvaluateLeg(net, Satellite, satPos, terminal, tunedHz, isUplinkLeg: false);
        var expectedCombined = SatcomLinkBudget.CombineCarrierToNoiseDensityDbHz(expectedUp.Cn0DbHz, expectedDown.Cn0DbHz);

        Assert.Equal(expectedCombined, result.CombinedCn0DbHz, precision: 3);
    }

    [Fact]
    public void DamaNetIgnoresTunedFrequencyAndAlwaysUsesItsOwnFixedPair()
    {
        var engine = BuildEngine(out _);
        var net = new SatcomNetDefinition
        {
            NetId = "dama", DisplayName = "dama", Waveform = SatcomWaveform.Dama5k,
            SelectionMode = SatelliteSelectionMode.AutoBestVisible,
            UplinkHz = 300_000_000.0, DownlinkHz = 260_000_000.0
        };

        // A tuned frequency wildly different from the net's fixed pair -- must be ignored entirely
        // for a DAMA net.
        var terminalWithTuned = new SatcomTerminalState(0.0, 0.0, 3000.0, 0.0, 0.0, 0.0, false,
            TunedFrequencyHz: 10_000_000_000.0);
        var terminalWithoutTuned = new SatcomTerminalState(0.0, 0.0, 3000.0, 0.0, 0.0, 0.0, false);

        engine.UpdateGeometry("client-tuned", net.NetId, terminalWithTuned, radioPowered: true, pttPressed: true,
            terrainLosClear: true, nowMs: 0);
        var resultWithTuned = engine.Evaluate("client-tuned", net.NetId, net, nowMs: 0);

        var engine2 = BuildEngine(out _);
        engine2.UpdateGeometry("client-plain", net.NetId, terminalWithoutTuned, radioPowered: true, pttPressed: true,
            terrainLosClear: true, nowMs: 0);
        var resultWithoutTuned = engine2.Evaluate("client-plain", net.NetId, net, nowMs: 0);

        Assert.Equal(resultWithoutTuned.CombinedCn0DbHz, resultWithTuned.CombinedCn0DbHz, precision: 3);
    }
}
