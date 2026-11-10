using OpenFreq.Common.Jamming;
using OpenFreq.Common.Satcom;
using OpenFreqAudio.Jamming;

namespace OpenFreq.Common.Tests.Satcom;

/// <summary>Phase 4: UHF SATCOM jamming (uplink I0, power robbing, local downlink, DAMA orderwire).</summary>
public class SatcomJammingTests
{
    private const double UplinkHz = 300_000_000.0;
    private const double DownlinkHz = 260_000_000.0;

    private static readonly SatcomSatelliteDefinition Satellite = new()
    {
        Id = "sat", DisplayName = "Sat", EphemerisMode = SatcomEphemerisMode.StaticGeo, StaticLongitudeDeg = 40.0
    };

    private static readonly SatcomSatellitePosition SatPos =
        new("sat", 0.0, 40.0, Satellite.StaticAltitudeMeters, 0, false);

    private static SatcomNetDefinition Net(SatcomWaveform waveform = SatcomWaveform.Dama5k,
        double? orderwireHz = null) => new()
    {
        NetId = "net", DisplayName = "net", Waveform = waveform, UplinkHz = UplinkHz, DownlinkHz = DownlinkHz,
        OrderwireUplinkHz = orderwireHz
    };

    // Caucasus-ish.
    private static SatcomTerminalState Aircraft(double lat = 42.0, double lon = 42.0, double alt = 6000.0) =>
        new(lat, lon, alt, 0.0, 0.0, 0.0, false);

    private static SatcomJammerSource Jammer(string preset, double centerKhz, double bandwidthKhz = 25.0,
        double lat = 43.0, double lon = 44.0, double powerFraction = 1.0, JammerTechnique? technique = null)
    {
        var resolved = JammerPresets.Resolve(new JammerDefinition
        {
            Id = preset + "-1", PresetId = preset, Latitude = lat, Longitude = lon, Technique = technique
        });
        return new SatcomJammerSource(resolved, lat, lon, JammerDetection.AntennaAltitudeMeters(resolved),
            [new JammerEmission(centerKhz, bandwidthKhz, powerFraction)]);
    }

    private static (SatcomLegResult Up, SatcomLegResult Down) Legs(SatcomNetDefinition net, SatcomTerminalState t) =>
        (SatcomLinkEngineCore.EvaluateLeg(net, Satellite, SatPos, t, UplinkHz, isUplinkLeg: true),
            SatcomLinkEngineCore.EvaluateLeg(net, Satellite, SatPos, t, DownlinkHz, isUplinkLeg: false));

    private static double EbN0(SatcomNetDefinition net, SatcomLegResult up, SatcomLegResult down) =>
        SatcomLinkBudget.EbN0Db(SatcomLinkBudget.CombineCarrierToNoiseDensityDbHz(up.Cn0DbHz, down.Cn0DbHz),
            net.BitRateBps);

    private static (SatcomLegResult Up, SatcomLegResult Down, SatcomJammingEffect Effect) Apply(
        SatcomNetDefinition net, SatcomTerminalState rx, params SatcomJammerSource[] jammers)
    {
        var (up, down) = Legs(net, rx);
        return SatcomJamming.Apply(up, down, jammers, net, Satellite, SatPos, rx, UplinkHz, DownlinkHz);
    }

    [Fact]
    public void NoJammersLeavesTheLinkUnchanged()
    {
        var net = Net();
        var (up, down) = Legs(net, Aircraft());
        var r = SatcomJamming.Apply(up, down, [], net, Satellite, SatPos, Aircraft(), UplinkHz, DownlinkHz);
        Assert.Equal(up, r.Uplink);
        Assert.Equal(down, r.Downlink);
        Assert.Same(SatcomJammingEffect.None, r.Effect);
    }

    [Fact]
    public void UplinkSpotJammerDeniesTheChannelForEveryUserInTheFootprint()
    {
        // Acceptance check 5: one ground uplink jammer, every user on the channel degraded, wherever they are.
        var net = Net();
        var tirada = Jammer("tirada_uhf", UplinkHz / 1000.0);

        foreach (var rx in new[] { Aircraft(), Aircraft(lat: 25.0, lon: 55.0, alt: 9000), Aircraft(lat: 50, lon: 10) })
        {
            var (up, down) = Legs(net, rx);
            Assert.True(EbN0(net, up, down) > net.AcquisitionEbN0ThresholdDb + 10, "clean link has margin");

            var r = Apply(net, rx, tirada);
            Assert.True(r.Effect.UplinkJsDb > 10, $"J/S {r.Effect.UplinkJsDb}");
            Assert.True(EbN0(net, r.Up, r.Down) < net.TrackingEbN0ThresholdDb, "jammed below tracking");
            Assert.Equal("tirada_uhf-1", r.Effect.DominantJammerId);
            Assert.True(r.Effect.DegradationDb > 20);
        }
    }

    [Fact]
    public void TacticalSpotJammerOnTheUplinkFrequencyAlsoKillsIt()
    {
        // research-02 §3: a ~100 W ground transmitter can occupy a UFO channel ("pirates"). R-934B spot, 0 dBi skyward.
        var r = Apply(Net(), Aircraft(), Jammer("r934b", UplinkHz / 1000.0));
        Assert.InRange(r.Effect.UplinkJsDb!.Value, 0, 20);
        Assert.True(EbN0(Net(), r.Up, r.Down) < Net().TrackingEbN0ThresholdDb);
    }

    [Fact]
    public void WidebandBarrageBarelyTouchesSatcom()
    {
        // A 225–400 MHz barrage spreads 500 W over 175 MHz: ~0.014 W in a 5 kHz channel. At the satellite that is
        // ~30 dB under a user's uplink: a receiver beyond the jammer's horizon only sees the small uplink share.
        var net = Net();
        var barrage = Jammer("r934b", 312_500, bandwidthKhz: 175_000);
        var far = Apply(net, Aircraft(lat: 30.0, lon: 30.0), barrage);
        Assert.Null(far.Effect.DownlinkJnrDb);
        Assert.True(far.Effect.UplinkJsDb < -20);
        Assert.InRange(far.Effect.DegradationDb, 0.1, 3.0);
    }

    [Fact]
    public void BarrageCoveringTheDownlinkHurtsReceiversWithinItsHorizon()
    {
        // The same barrage also covers the 260 MHz downlink; ~190 km from an aircraft in line of sight, its
        // in-channel slice is well above the weak satellite downlink even through the fuselage shielding.
        var net = Net();
        var near = Apply(net, Aircraft(), Jammer("r934b", 312_500, bandwidthKhz: 175_000));
        Assert.True(near.Effect.DownlinkJnrDb > 5);
        Assert.True(near.Effect.DegradationDb > 3);
    }

    [Fact]
    public void JammerOffTheChannelDoesNothing()
    {
        var r = Apply(Net(), Aircraft(), Jammer("tirada_uhf", 310_000));
        Assert.False(r.Effect.Any);
        Assert.Null(r.Effect.UplinkJsDb);
    }

    [Fact]
    public void JammerThatCannotSeeTheSatelliteDoesNothing()
    {
        // Antipodal to the satellite (lon 40 → −140): below its horizon.
        var r = Apply(Net(), Aircraft(), Jammer("tirada_uhf", UplinkHz / 1000.0, lat: 0, lon: -140));
        Assert.False(r.Effect.Any);
    }

    [Fact]
    public void PowerRobbingFollowsTheHardLimiterShare()
    {
        var net = Net();
        var rx = Aircraft();
        var (up, _) = Legs(net, rx);
        // Pick a jammer power fraction that lands J ≈ S at the satellite.
        var probe = Jammer("tirada_uhf", UplinkHz / 1000.0);
        var js = SatcomJamming.Apply(up, Legs(net, rx).Down, [probe], net, Satellite, SatPos, rx, UplinkHz,
            DownlinkHz).Effect.UplinkJsDb!.Value;
        var equal = Jammer("tirada_uhf", UplinkHz / 1000.0, powerFraction: Math.Pow(10, -js / 10));

        var r = Apply(net, rx, equal);
        Assert.Equal(0.0, r.Effect.UplinkJsDb!.Value, 1);
        // S/(S+J+N) vs S/(S+N) with J = S ≫ N: 3 dB, plus noise-limiter suppression of 1.05 × ½.
        Assert.Equal(3.0 + SatcomJamming.MaxNoiseSuppressionDb / 2, r.Effect.DownlinkRobbingLossDb, 1);
    }

    [Theory]
    [InlineData(JammerTechnique.Noise, SatcomJamming.MaxNoiseSuppressionDb)]
    [InlineData(JammerTechnique.Tone, SatcomJamming.MaxCarrierSuppressionDb)]
    public void LimiterSuppressionSaturatesPerTechnique(JammerTechnique technique, double max)
    {
        Assert.InRange(SatcomJamming.LimiterSuppressionDb(-30, technique), 0, 0.01);
        Assert.Equal(max / 2, SatcomJamming.LimiterSuppressionDb(0, technique), 3);
        Assert.Equal(max, SatcomJamming.LimiterSuppressionDb(40, technique), 2);
    }

    [Fact]
    public void LocalDownlinkJammerHurtsOnlyReceiversItReaches()
    {
        var net = Net();
        var r934bOnDownlink = Jammer("r934b", DownlinkHz / 1000.0, lat: 42.0, lon: 42.5);

        var near = Apply(net, Aircraft(alt: 3000), r934bOnDownlink);
        Assert.NotNull(near.Effect.DownlinkJnrDb);
        Assert.Null(near.Effect.UplinkJsDb);
        Assert.True(EbN0(net, near.Up, near.Down) < net.TrackingEbN0ThresholdDb);

        // Far beyond the radio horizon (12 m mast, 3000 m aircraft ≈ 240 km): untouched.
        var far = Apply(net, Aircraft(lat: 30.0, lon: 30.0, alt: 3000), r934bOnDownlink);
        Assert.False(far.Effect.Any);
    }

    [Fact]
    public void AirborneReceiverIsShieldedFromAGroundDownlinkJammer()
    {
        var net = Net();
        var jammer = Jammer("r934b", DownlinkHz / 1000.0, lat: 42.0, lon: 42.0);
        var ground = new SatcomTerminalState(42.0, 42.3, jammer.AltitudeMeters, 0.0, 0.0, 0.0, false);
        var air = new SatcomTerminalState(42.0, 42.3, 4000, 0.0, 0.0, 0.0, false);

        var jGround = SatcomJamming.DownlinkJammerDbw(jammer, net, ground, DownlinkHz)!.Value;
        var jAir = SatcomJamming.DownlinkJammerDbw(jammer, net, air, DownlinkHz)!.Value;
        // Same ground range; the airborne path is a little longer, plus 10 dB of fuselage shielding.
        Assert.InRange(jGround - jAir, SatcomJamming.AircraftShieldingDb, SatcomJamming.AircraftShieldingDb + 1);
    }

    [Fact]
    public void DamaOrderwireJammingIsDetectedOnItsOwnFrequency()
    {
        const double orderwireHz = 305_000_000.0;
        var net = Net(orderwireHz: orderwireHz);
        var onOrderwire = Jammer("tirada_uhf", orderwireHz / 1000.0);

        var ow = SatcomJamming.OrderwireJammed([onOrderwire], net, Satellite, SatPos);
        Assert.True(ow.Jammed);
        Assert.Equal("tirada_uhf-1", ow.JammerId);

        // The traffic channel itself is untouched.
        Assert.False(Apply(net, Aircraft(), onOrderwire).Effect.Any);

        // Dedicated nets have no orderwire.
        Assert.False(SatcomJamming.OrderwireJammed([onOrderwire], Net(SatcomWaveform.Dedicated5k, orderwireHz),
            Satellite, SatPos).Jammed);

        // A weak barrage does not take the orderwire.
        Assert.False(SatcomJamming.OrderwireJammed([Jammer("r934b", 312_500, bandwidthKhz: 175_000)], net,
            Satellite, SatPos).Jammed);
    }

    [Fact]
    public void OrderwireDefaultsToTheNetsUplinkChannel()
    {
        Assert.True(SatcomJamming.OrderwireJammed([Jammer("tirada_uhf", UplinkHz / 1000.0)], Net(), Satellite,
            SatPos).Jammed);
    }

    [Fact]
    public void SkyGainDefaultsToZeroExceptForSatcomUplinkJammers()
    {
        Assert.Equal(0.0, JammerPresets.Resolve(new JammerDefinition { PresetId = "r934b" }).SatcomAntennaGainDbi);
        Assert.Equal(22.0, JammerPresets.Resolve(new JammerDefinition { PresetId = "tirada_uhf" }).SatcomAntennaGainDbi);
        Assert.Equal(5.0, JammerPresets.Resolve(new JammerDefinition
        {
            PresetId = "r934b", SatcomAntennaGainDbi = 5.0
        }).SatcomAntennaGainDbi);
    }
}
