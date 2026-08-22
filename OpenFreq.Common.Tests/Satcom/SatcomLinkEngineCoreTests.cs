using OpenFreq.Common.Satcom;

namespace OpenFreq.Common.Tests.Satcom;

public class SatcomLinkEngineCoreTests
{
    private static readonly SatcomSatelliteDefinition Satellite = new()
    {
        Id = "test-sat", DisplayName = "Test", EphemerisMode = SatcomEphemerisMode.StaticGeo,
        StaticLongitudeDeg = 0.0
    };

    private static readonly SatcomNetDefinition Net = new()
    {
        NetId = "n", DisplayName = "n", MinElevationMaskDeg = 3.0
    };

    private static SatcomSatellitePosition SatPos() => new(
        Satellite.Id, 0.0, Satellite.StaticLongitudeDeg, Satellite.StaticAltitudeMeters, 0, false);

    [Fact]
    public void GoodOverheadGeometryProducesUsableLeg()
    {
        var terminal = new SatcomTerminalState(0.0, 0.0, 3000.0, 0.0, 0.0, 0.0, false);
        var leg = SatcomLinkEngineCore.EvaluateLeg(Net, Satellite, SatPos(), terminal, 300_000_000, isUplinkLeg: true);

        Assert.True(leg.Usable);
        Assert.True(leg.Cn0DbHz > 0);
    }

    [Fact]
    public void BelowHorizonTerminalIsUnusable()
    {
        // Antipodal to the satellite's longitude -- geometrically below the horizon.
        var terminal = new SatcomTerminalState(0.0, 180.0, 3000.0, 0.0, 0.0, 0.0, false);
        var leg = SatcomLinkEngineCore.EvaluateLeg(Net, Satellite, SatPos(), terminal, 300_000_000, isUplinkLeg: true);

        Assert.False(leg.Usable);
        Assert.False(leg.AboveElevationMask);
    }

    [Fact]
    public void WeakUplinkDegradesCombinedResultEvenWithGoodDownlink()
    {
        var txBanked = new SatcomTerminalState(0.0, 0.0, 3000.0, 0.0, 0.0, 60.0 * System.Math.PI / 180.0, false);
        var rxLevel = new SatcomTerminalState(0.0, 1.0, 3000.0, 0.0, 0.0, 0.0, false);

        var up = SatcomLinkEngineCore.EvaluateLeg(Net, Satellite, SatPos(), txBanked, Net.UplinkHz, isUplinkLeg: true);
        var down = SatcomLinkEngineCore.EvaluateLeg(Net, Satellite, SatPos(), rxLevel, Net.DownlinkHz, isUplinkLeg: false);

        Assert.True(up.Cn0DbHz < down.Cn0DbHz, "Banked uplink terminal should have worse C/N0 than level downlink terminal");

        var tracker = new SatcomLinkQualityTracker();
        var combined = SatcomLinkEngineCore.Combine(Satellite.Id, Satellite.DisplayName, up, down, Net,
            tracker.Update(SatcomLinkBudget.EbN0Db(
                SatcomLinkBudget.CombineCarrierToNoiseDensityDbHz(up.Cn0DbHz, down.Cn0DbHz), Net.BitRateBps),
                Net, nowMs: 0));

        // Combined result must reflect the worse (uplink) leg, not be rescued by the good downlink.
        Assert.True(combined.CombinedCn0DbHz <= down.Cn0DbHz);
    }

    [Fact]
    public void TerminalToTerminalSeparationAloneDoesNotDetermineQuality()
    {
        // Both receivers see the same satellite from near-boresight geometry; only their distance
        // from EACH OTHER differs. Terminal-to-terminal distance must never enter the calculation
        // -- each leg is evaluated against the satellite independently.
        var rxNear = new SatcomTerminalState(0.0, 0.5, 3000.0, 0.0, 0.0, 0.0, false);
        var rxFar = new SatcomTerminalState(5.0, 5.0, 3000.0, 0.0, 0.0, 0.0, false);

        var near = SatcomLinkEngineCore.EvaluateLeg(Net, Satellite, SatPos(), rxNear, Net.DownlinkHz, isUplinkLeg: false);
        var far = SatcomLinkEngineCore.EvaluateLeg(Net, Satellite, SatPos(), rxFar, Net.DownlinkHz, isUplinkLeg: false);

        Assert.True(near.Usable);
        Assert.True(far.Usable);
    }
}
