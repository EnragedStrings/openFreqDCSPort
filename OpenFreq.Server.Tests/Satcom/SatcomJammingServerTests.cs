using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Common;
using OpenFreq.Common.Jamming;
using OpenFreq.Common.Satcom;
using OpenFreqServer.Jamming;
using OpenFreqServer.Satcom;

namespace OpenFreqServer.Tests.Satcom;

/// <summary>Phase 4 on the server: the link engine with jammers, DAMA orderwire denial, and the jammer feed.</summary>
public class SatcomJammingServerTests
{
    private const double UplinkHz = 300_000_000.0;

    private static readonly SatcomSatelliteDefinition Satellite = new()
    {
        Id = "test-sat", DisplayName = "Test", EphemerisMode = SatcomEphemerisMode.StaticGeo, StaticLongitudeDeg = 40.0
    };

    private static readonly SatcomNetDefinition DamaNet = new()
    {
        NetId = "dama", DisplayName = "dama", Waveform = SatcomWaveform.Dama5k,
        SelectionMode = SatelliteSelectionMode.Assigned, AssignedSatelliteId = "test-sat",
        UplinkHz = UplinkHz, DownlinkHz = 260_000_000.0
    };

    private static SatcomJammerSource Tirada(double centerHz = UplinkHz)
    {
        var resolved = JammerPresets.Resolve(new JammerDefinition
        {
            Id = "tirada", PresetId = "tirada_uhf", Latitude = 43.0, Longitude = 44.0
        });
        return new SatcomJammerSource(resolved, 43.0, 44.0, JammerDetection.AntennaAltitudeMeters(resolved),
            [new JammerEmission(centerHz / 1000.0, 25.0, 1.0)]);
    }

    private static SatcomLinkEngine Engine(IReadOnlyList<SatcomJammerSource> jammers)
    {
        var dir = Path.Combine(Path.GetTempPath(), "openfreq-satcom-jamming-test-" + Guid.NewGuid());
        var ephemeris = new SatcomEphemerisService([Satellite], new SatcomServerConfig { EphemerisCacheDirectory = dir },
            NullLogger.Instance);
        ephemeris.PropagateAll();
        return new SatcomLinkEngine([Satellite], ephemeris, new SatcomSatelliteSelector()) { JammerSources = () => jammers };
    }

    private static readonly SatcomTerminalState Aircraft = new(42.0, 42.0, 6000.0, 0.0, 0.0, 0.0, false);

    [Fact]
    public void UnjammedLinkIsUnaffectedByAnEmptyJammerFeed()
    {
        var engine = Engine([]);
        engine.UpdateGeometry("c1", DamaNet.NetId, Aircraft, true, false, true, 0);
        var link = engine.Evaluate("c1", DamaNet.NetId, DamaNet, 0);
        Assert.True(link.LinkAvailable);
        Assert.False(link.Jamming.Any);
        Assert.Equal("None", SatcomServerCoordinator.ClassifyJamming(link));
    }

    [Fact]
    public void UplinkJammerTakesTheLinkDownAndIsNamedAsTheCause()
    {
        var engine = Engine([Tirada()]);
        engine.UpdateGeometry("c1", DamaNet.NetId, Aircraft, true, false, true, 0);

        SatcomLinkResult link = null!;
        for (var t = 0L; t <= 12_000; t += 500) // past the 4 s holdover
            link = engine.Evaluate("c1", DamaNet.NetId, DamaNet, t);

        Assert.False(link.LinkAvailable);
        Assert.Equal(SatcomAcquisitionFailureReason.Jammed, link.FailureReason);
        Assert.Contains("tirada", link.UnavailableReason);
        Assert.Equal("tirada", link.Jamming.DominantJammerId);
        Assert.True(link.Jamming.OrderwireJammed); // the orderwire shares the jammed uplink channel by default
        Assert.Equal("Jammed", SatcomServerCoordinator.ClassifyJamming(link));
    }

    [Fact]
    public void JammingTheOrderwireAloneLeavesTheTrafficLinkUp()
    {
        var net = new SatcomNetDefinition
        {
            NetId = "dama-ow", DisplayName = "dama-ow", Waveform = SatcomWaveform.Dama5k,
            SelectionMode = SatelliteSelectionMode.Assigned, AssignedSatelliteId = "test-sat",
            UplinkHz = UplinkHz, DownlinkHz = 260_000_000.0, OrderwireUplinkHz = 305_000_000.0
        };
        var engine = Engine([Tirada(305_000_000.0)]);
        engine.UpdateGeometry("c1", net.NetId, Aircraft, true, false, true, 0);
        var link = engine.Evaluate("c1", net.NetId, net, 0);

        Assert.True(link.LinkAvailable);
        Assert.True(link.Jamming.OrderwireJammed);
        Assert.Equal("Jammed", SatcomServerCoordinator.ClassifyJamming(link));
    }

    // ---------------------------------------------------------------- DAMA orderwire

    private static SatcomLinkResult GoodLink()
    {
        var leg = new SatcomLegResult(45, 90, 36_000_000, 0, 150, 60, 0.12, true, false, "");
        return new SatcomLinkResult
        {
            SatelliteId = "s", SatelliteName = "s", Uplink = leg, Downlink = leg,
            CombinedCn0DbHz = 60, EbN0Db = 20, RawBer = 1e-6, PostFecBer = 1e-9, FrameErrorRate = 0.0,
            PropagationLatencySeconds = 0.24, QualityState = SatcomLinkQualityState.Good, LinkAvailable = true,
            FailureReason = SatcomAcquisitionFailureReason.None, UnavailableReason = ""
        };
    }

    private static long ToReady(DamaNetworkController c, SatcomNetDefinition net)
    {
        var t = 0L;
        c.Update("c1", net, true, GoodLink(), false, false, 0, t);
        t += (long)(SatcomDamaStateMachine.SearchingSeconds * 1000);
        c.Update("c1", net, true, GoodLink(), false, false, 0, t);
        t += (long)(SatcomDamaStateMachine.SynchronizingSeconds * 1000);
        Assert.Equal(DamaState.Ready, c.Update("c1", net, true, GoodLink(), false, false, 0, t));
        return t;
    }

    [Fact]
    public void JammedOrderwireDeniesChannelRequests()
    {
        var c = new DamaNetworkController();
        var t = ToReady(c, DamaNet);

        Assert.Equal(DamaState.Requesting, c.Update("c1", DamaNet, true, GoodLink(), true, false, 0, t + 10, true));
        Assert.Equal(DamaState.ServiceDenied, c.Update("c1", DamaNet, true, GoodLink(), true, false, 0, t + 20, true));
        Assert.Null(c.CurrentSlot("c1", DamaNet.NetId));
    }

    [Fact]
    public void JammedOrderwireDropsNetworkSyncAfterOneAndAHalfFramesAndRecoversWhenCleared()
    {
        var c = new DamaNetworkController();
        var t = ToReady(c, DamaNet);
        var lossMs = (long)(DamaNetworkController.OrderwireLossFrames * Dama5kHzScheduler.FramePeriodSecondsConst * 1000);

        Assert.Equal(DamaState.Ready, c.Update("c1", DamaNet, true, GoodLink(), false, false, 0, t + 1, true));
        Assert.Equal(DamaState.Ready, c.Update("c1", DamaNet, true, GoodLink(), false, false, 0, t + lossMs - 10, true));
        Assert.Equal(DamaState.LostSync, c.Update("c1", DamaNet, true, GoodLink(), false, false, 0, t + lossMs + 10, true));

        // Jammer gone: re-acquire through Searching → Synchronizing → Ready.
        var now = t + lossMs + 1000;
        Assert.Equal(DamaState.Searching, c.Update("c1", DamaNet, true, GoodLink(), false, false, 0, now));
        now += (long)(SatcomDamaStateMachine.SearchingSeconds * 1000);
        Assert.Equal(DamaState.Synchronizing, c.Update("c1", DamaNet, true, GoodLink(), false, false, 0, now));
        now += (long)(SatcomDamaStateMachine.SynchronizingSeconds * 1000);
        Assert.Equal(DamaState.Ready, c.Update("c1", DamaNet, true, GoodLink(), false, false, 0, now));
    }

    [Fact]
    public void DedicatedNetsHaveNoOrderwireToJam()
    {
        var net = new SatcomNetDefinition { NetId = "ded", DisplayName = "ded", Waveform = SatcomWaveform.Dedicated5k };
        var c = new DamaNetworkController();
        var t = ToReady(c, net);
        Assert.Equal(DamaState.Requesting, c.Update("c1", net, true, GoodLink(), true, false, 0, t + 10, true));
        Assert.Equal(DamaState.Assigned, c.Update("c1", net, true, GoodLink(), true, false, 0, t + 20, true));
        Assert.Equal(DamaState.Ready, c.Update("c1", net, true, GoodLink(), false, false, 0, t + 60_000, true));
    }

    // ---------------------------------------------------------------- jammer feed

    [Fact]
    public void CoordinatorFeedsOnlyRadiatingGeodeticallyPlacedJammers()
    {
        var coordinator = new JammingCoordinator(new JammingServerConfig
        {
            Enabled = true,
            Jammers =
            [
                new JammerDefinition
                {
                    Id = "geo", PresetId = "tirada_uhf", Latitude = 43, Longitude = 44,
                    Targeting = new JammerTargeting { SpotFrequenciesKhz = [300_000] }
                },
                new JammerDefinition
                {
                    Id = "dcs-only", PresetId = "tirada_uhf", DcsPosition = new JammerVector3(0, 0, 0),
                    Targeting = new JammerTargeting { SpotFrequenciesKhz = [300_000] }
                },
                new JammerDefinition
                {
                    Id = "off", PresetId = "tirada_uhf", Latitude = 43, Longitude = 44, Enabled = false,
                    Targeting = new JammerTargeting { SpotFrequenciesKhz = [300_000] }
                }
            ]
        }, NullLogger.Instance);

        Assert.Empty(coordinator.SatcomSources);
        coordinator.Snapshot();
        var source = Assert.Single(coordinator.SatcomSources);
        Assert.Equal("geo", source.Id);
        Assert.Single(source.Emissions);
        Assert.Equal(22.0, source.Resolved.SatcomAntennaGainDbi);
    }
}
