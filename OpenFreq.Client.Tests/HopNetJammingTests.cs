using FalconBmsDataService.Models;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Client.Models;
using OpenFreq.Client.Models.Dcs;
using OpenFreq.Client.Services.Eccm;
using OpenFreq.Client.Services.Interfaces;
using OpenFreq.Client.Services.Jamming;
using OpenFreq.Common;
using OpenFreq.Common.Eccm;
using OpenFreq.Common.Jamming;
using OpenFreqAudio;
using OpenFreqAudio.Eccm;
using OpenFreqAudio.Jamming;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Jamming against HAVE QUICK / SINCGARS slots (docs/jamming/HAVE_QUICK.md, JAMMING_DESIGN acceptance checks 1, 3,
/// 4): JammingService hands playback the hop hit fraction μ from the slot's actual hopset, or the follower's dwell
/// fraction.
/// </summary>
public class HopNetJammingTests
{
    private const int UhfKhz = 251_000;
    private const int FmKhz = 40_000;
    private const double Nm = 1852.0;
    private const string Wod = "300.050 251.000 277.775 312.600 225.025 399.975";
    private static readonly Guid Slot = Guid.NewGuid();

    private readonly DateTime _now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private readonly List<IReadOnlyList<JammerContribution>> _pushed = [];

    private async Task<(ServiceHarness H, JammingService J)> ReceiverAsync(int frequencyKhz)
    {
        var h = new ServiceHarness();
        await h.InitializeAsync(IOpenFreqService.Mode.DCS);
        h.Client.IsAuthenticated.Returns(true);
        h.Dcs.State.Returns(ServiceState.Connected);
        h.Dcs.Position.Returns(new DcsVector3(0, 3000, 0));
        h.Dcs.Latitude.Returns(42.0);
        h.Dcs.Longitude.Returns(42.0);
        h.Dcs.AltitudeMsl.Returns(3000.0);
        var station = new RadioStationData
        {
            Type = RadioStationData.RadioStationType.DCS, Preset = RadioStationPresets.FighterF16, Ppm = 0
        };
        await h.Service.JoinFrequencyAsync(frequencyKhz, Slot, station);
        h.Playback.When(p => p.SetSlotInterference(Arg.Any<int>(), Arg.Any<Guid>(),
                Arg.Any<IReadOnlyList<JammerContribution>>()))
            .Do(ci => _pushed.Add(ci.ArgAt<IReadOnlyList<JammerContribution>>(2)));
        return (h, new JammingService(h.Service, h.Dcs, NullLogger<JammingService>.Instance, () => _now));
    }

    private static void SendCatalog(ServiceHarness h, JammerDefinition definition)
    {
        var resolved = JammerPresets.Resolve(definition);
        var entry = new JammerCatalogEntryDto
        {
            Definition = resolved, Active = true, Emissions = ScriptedTargeting.Emissions(resolved)
        };
        h.Client.JammerCatalogReceived += Raise.EventWith(h.Client,
            new JammerCatalogEventArgs(new JammerCatalogUpdateMessage { Jammers = [entry] }));
    }

    /// <summary>A generic 50 W, 0 dBi jammer <paramref name="northMeters"/> north of the receiver.</summary>
    private static JammerDefinition Jammer(double northMeters, JammerTargeting targeting, bool responsive = false,
        float reactionMs = 0) => new()
    {
        Id = "jam", TxPowerW = 50, AntennaGainDbi = 0, FeederLossDb = 0, MaxSimultaneousSpots = 4,
        DcsPosition = new JammerVector3(northMeters, 3000, 0), Targeting = targeting,
        Responsive = responsive, ReactionMs = reactionMs
    };

    private EccmResolution Hq(string net, EccmCommPlan? plan = null) =>
        EccmResolver.Resolve(new EccmSlotSettings { Mode = HopMode.HaveQuick, Net = net, ManualWod = Wod }, plan, _now);

    private IReadOnlyList<JammerContribution> Tick(JammingService j)
    {
        _pushed.Clear();
        j.Tick();
        return _pushed.Count > 0 ? _pushed[^1] : [];
    }

    [Fact]
    public async Task Check1_SpotJammerKillsTheSingleChannelNet_ButBarelyTouchesHaveQuick()
    {
        // 50 W spot at 50 nm; the talker (10 W) is at 100 nm.
        var nato = Hopset.Nato();
        var spot = nato.ChannelsKhz.First(f => f > 250_000);
        var (h, j) = await ReceiverAsync(spot);
        SendCatalog(h, Jammer(50 * Nm, new JammerTargeting { SpotFrequenciesKhz = [spot] }));

        var single = Assert.Single(Tick(j));
        Assert.Equal(1f, single.HopHitFraction);
        var rxSens = RadioStationPresets.FighterF16.GetRxSensitivity(BackgroundNoiseGenerator.RadioType.UHF_AM);
        var talkerSnr = 40.0 - JammingMath.FreeSpacePathLossDb(100 * Nm / 1000, spot / 1000.0) - rxSens;
        Assert.True(single.JnrDb - talkerSnr >= 0, $"J/S {single.JnrDb - talkerSnr:F1} dB");
        Assert.Equal(JammingBadge.Jammed, JammingService.ClassifyBadge(single.JnrDb, talkerSnr, false));

        h.Service.SetSlotEccm(spot, Slot, Hq("A12.325"));
        var hq = Assert.Single(Tick(j));
        Assert.Equal(1.0 / 2000, hq.HopHitFraction, 6); // 0.05 % of hops
        Assert.Equal(single.JnrDb, hq.JnrDb, 3);       // the hops it hits are hit just as hard
        Assert.Equal(JammingBadge.None, JammingService.ClassifyBadge(hq.JnrDb, talkerSnr, false, hq.HopHitFraction));
    }

    [Fact]
    public async Task Check1_SpotOffTheHopset_DoesNothingToHaveQuick()
    {
        var (h, j) = await ReceiverAsync(UhfKhz);
        var off = Enumerable.Range(0, 7000).Select(i => 225_000 + 25 * i).First(f => f > 300_000 && !Hopset.Nato().Contains(f));
        SendCatalog(h, Jammer(50 * Nm, new JammerTargeting { SpotFrequenciesKhz = [off] }));
        h.Service.SetSlotEccm(UhfKhz, Slot, Hq("A12.325"));
        Assert.Empty(Tick(j));
    }

    [Fact]
    public async Task Check3_PartialBandTenPercent()
    {
        var band = new JammerTargeting { Bands = [new JammerBand(300_000, 317_500)] };
        var (h, j) = await ReceiverAsync(305_000);
        SendCatalog(h, Jammer(20_000, band));

        Assert.Equal(1f, Assert.Single(Tick(j)).HopHitFraction); // a single-channel net inside the band: every "hop"

        h.Service.SetSlotEccm(305_000, Slot, Hq("A12.325"));
        var hq = Assert.Single(Tick(j));
        Assert.InRange(hq.HopHitFraction, 0.095, 0.11);
        Assert.False(hq.HopFollower);
    }

    [Fact]
    public async Task Check4_FollowerJamsMostOfEachDwell()
    {
        var band = new JammerTargeting { Bands = [new JammerBand(225_000, 400_000)] };
        var (h, j) = await ReceiverAsync(UhfKhz);
        SendCatalog(h, Jammer(15_000, band, responsive: true, reactionMs: 2));
        h.Service.SetSlotEccm(UhfKhz, Slot, Hq("A12.325"));

        var c = Assert.Single(Tick(j));
        Assert.True(c.HopFollower);
        Assert.True(c.Responsive);
        // 1 − (2 ms + 2·15 km / c) · 100 hops/s
        Assert.Equal(1 - (2 + 2 * 15_000.0 / 299_792_458.0 * 1000) * 0.1, c.HopHitFraction, 4);
        Assert.InRange(c.HopHitFraction, 0.78, 0.8);
    }

    [Fact]
    public async Task FmtNet_OneJammedFrequencyOfEight_Is12Point5Percent()
    {
        var fmt = Hopset.DefaultFmtFrequenciesKhz;
        var (h, j) = await ReceiverAsync(UhfKhz);
        SendCatalog(h, Jammer(50 * Nm, new JammerTargeting { SpotFrequenciesKhz = [fmt[2]] }));
        h.Service.SetSlotEccm(UhfKhz, Slot, Hq("A00.025"));
        Assert.Equal(0.125, Assert.Single(Tick(j)).HopHitFraction, 6);
    }

    [Fact]
    public async Task Sincgars_LockoutRemovesThePartialBandJammer()
    {
        var (h, j) = await ReceiverAsync(FmKhz);
        SendCatalog(h, Jammer(20_000, new JammerTargeting { Bands = [new JammerBand(40_000, 45_000)] }));

        h.Service.SetSlotEccm(FmKhz, Slot,
            EccmResolver.Resolve(new EccmSlotSettings { Mode = HopMode.Sincgars, Net = "F001" }, null, _now));
        var open = Assert.Single(Tick(j)).HopHitFraction;
        Assert.InRange(open, 0.08, 0.09);

        var plan = new EccmCommPlan { Sincgars = new EccmSincgarsPlan { Lockouts = [new JammerBand(40_000, 45_000)] } };
        h.Service.SetSlotEccm(FmKhz, Slot,
            EccmResolver.Resolve(new EccmSlotSettings { Mode = HopMode.Sincgars, Net = "F001" }, plan, _now));
        Assert.Empty(Tick(j));
    }

    [Fact]
    public async Task EccmService_AppliesSettings_AndReappliesWhenThePlanArrives()
    {
        var (h, _) = await ReceiverAsync(UhfKhz);
        var applied = new List<EccmResolution>();
        h.Playback.When(p => p.SetSlotHopNet(Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<HopSlotState?>()))
            .Do(_ => applied.Add(h.Service.GetSlotEccm(Slot)!));
        using var eccm = new EccmService(h.Service, NullLogger<EccmService>.Instance, () => _now);

        var settings = new EccmSlotSettings { Mode = HopMode.HaveQuick, Net = "A12.325" };
        var first = eccm.Apply(Slot, UhfKhz, settings);
        Assert.Equal(EccmReadiness.NoWod, first.Readiness);

        var fill = new EccmFill { MissionDate = "2026-11-09", Mwod = [new EccmWodEntry { Date = "2026-11-09", Wod = Wod }] };
        h.Client.EccmCommPlanReceived += Raise.Event<EventHandler<EccmCommPlanUpdateMessage>>(h.Client,
            new EccmCommPlanUpdateMessage { Plan = fill.ToCommPlan(_now) });

        var now = h.Service.GetSlotEccm(Slot)!;
        Assert.True(now.IsReady);
        Assert.Equal(Hq("A12.325").State!.Identity.KeyFingerprint, now.State!.Identity.KeyFingerprint);
        Assert.Equal(2, applied.Count);

        eccm.Apply(Slot, UhfKhz, EccmSlotSettings.Off);
        Assert.Null(h.Service.GetSlotEccm(Slot));
    }
}
