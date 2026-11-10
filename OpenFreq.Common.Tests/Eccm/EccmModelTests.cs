using System.Text.Json;
using OpenFreq.Common.Eccm;
using OpenFreq.Common.Jamming;
using OpenFreqAudio.Eccm;

namespace OpenFreq.Common.Tests.Eccm;

public class EccmModelTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 11, 9);
    private const string Wod = "300.050 251.000 277.775 312.600 225.025 399.975";

    // ------------------------------------------------------------------ WOD / MWOD

    [Fact]
    public void Wod_ParsesFrequencyAndDigitForms()
    {
        var a = HaveQuickWod.TryParse(Wod, out var err);
        Assert.NotNull(a);
        Assert.Null(err);
        Assert.Equal("300050", a!.Segments[0]);
        Assert.Equal(Wod, a.ToString());

        var b = HaveQuickWod.TryParse("300050,251000,277775,312600,225025,399975", out _);
        Assert.Equal(a.Fingerprint(Day), b!.Fingerprint(Day));
    }

    [Theory]
    [InlineData("")]
    [InlineData("300.050 251.000 277.775 312.600 225.025")] // 5 segments
    [InlineData("300.050 251.000 277.775 312.600 225.025 399.97")] // 5 digits
    [InlineData("300.050 251.000 277.775 312.600 225.025 39X.975")]
    public void Wod_RejectsInvalid(string text)
    {
        Assert.Null(HaveQuickWod.TryParse(text, out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public void Wod_FingerprintIsDayKeyedAndHidesDigits()
    {
        var wod = HaveQuickWod.TryParse(Wod, out _)!;
        var fp = wod.Fingerprint(Day);
        Assert.Equal(16, fp.Length);
        Assert.Equal(fp, wod.Fingerprint(Day));
        Assert.NotEqual(fp, wod.Fingerprint(Day.AddDays(1)));
        Assert.DoesNotContain("300050", fp);
    }

    [Fact]
    public void Mwod_HoldsSixDays()
    {
        var set = new MwodSet();
        var wod = HaveQuickWod.TryParse(Wod, out _)!;
        for (var i = 0; i < 8; i++) set.Load(Day.AddDays(i), wod);
        Assert.Equal(6, set.Count);
        Assert.Null(set.ForDay(Day)); // oldest pushed out
        Assert.NotNull(set.ForDay(Day.AddDays(7)));
    }

    // ------------------------------------------------------------------ net numbers

    [Theory]
    [InlineData("A12.325", HaveQuickNetKind.Nato)]
    [InlineData("a12.350", HaveQuickNetKind.NonNato)]
    [InlineData("12.300", HaveQuickNetKind.HqI)]
    [InlineData("A00.000", HaveQuickNetKind.Training)]
    [InlineData("A00.400", HaveQuickNetKind.Training)]
    [InlineData("A00.025", HaveQuickNetKind.Fmt)]
    [InlineData("A01.525", HaveQuickNetKind.Fmt)]
    [InlineData("A00.500", HaveQuickNetKind.HqI)]
    [InlineData("A01.550", HaveQuickNetKind.NonNato)]
    [InlineData("A12.375", HaveQuickNetKind.Invalid)]
    [InlineData("A99.975", HaveQuickNetKind.Invalid)]
    public void Net_Classifies(string text, HaveQuickNetKind kind)
    {
        var net = HaveQuickNet.TryParse(text);
        Assert.NotNull(net);
        Assert.Equal(kind, net!.Value.Kind);
    }

    [Theory]
    [InlineData("A12.310")]
    [InlineData("A100.000")]
    [InlineData("B12.300")]
    [InlineData("")]
    public void Net_RejectsMalformed(string text) => Assert.Null(HaveQuickNet.TryParse(text));

    [Fact]
    public void Net_FormatsCanonically() => Assert.Equal("A02.025", HaveQuickNet.TryParse("2.025")!.Value.ToString());

    // ------------------------------------------------------------------ hopsets and μ

    [Fact]
    public void Hopsets_HaveTheEstimatedSizesAndAvoidGuardAndIls()
    {
        var nato = Hopset.Nato();
        var nonNato = Hopset.NonNato();
        Assert.Equal(2000, nato.Count);
        Assert.Equal(4000, nonNato.Count);
        foreach (var hs in new[] { nato, nonNato })
        {
            Assert.DoesNotContain(hs.ChannelsKhz, f => f is >= 242_900 and <= 243_100);
            Assert.DoesNotContain(hs.ChannelsKhz, f => f is >= 328_600 and <= 335_400);
            Assert.All(hs.ChannelsKhz, f => Assert.InRange(f, 225_000, 399_975));
        }
        Assert.Equal(2320, Hopset.Sincgars().Count);
        Assert.Equal(8, Hopset.Fmt(null).Count);
    }

    [Fact]
    public void Mu_SpotOnACombatHopsetIsOneChannel()
    {
        var nato = Hopset.Nato();
        var target = nato.ChannelsKhz[777];
        Assert.Equal(1.0 / 2000, nato.HitFraction([(target, 25.0)]), 9); // 0.05 % of hops
        // a spot on a raster channel the hopset doesn't use hits nothing
        var unused = Enumerable.Range(0, 7000).Select(i => 225_000 + 25 * i).First(f => !nato.Contains(f) && f > 300_000);
        Assert.Equal(0.0, nato.HitFraction([(unused, 25.0)]));
    }

    [Fact]
    public void Mu_PartialBandTenPercent()
    {
        // 10 % of the 225–400 band, clear of the excluded spectrum
        var mu = Hopset.Nato().HitFraction([(300_000 + 8_750, 17_500)]);
        Assert.InRange(mu, 0.095, 0.11);
        var barrage = Hopset.NonNato().HitFraction([(312_500, 175_000)]);
        Assert.Equal(1.0, barrage, 6);
    }

    [Fact]
    public void Mu_FmtNetWithOneOfEightJammedIs12Point5Percent()
    {
        var fmt = Hopset.Fmt(null);
        Assert.Equal(0.125, fmt.HitFraction([(fmt.ChannelsKhz[3], 25.0)]), 9);
    }

    [Fact]
    public void Mu_SincgarsLockoutRemovesJammedSubBand()
    {
        var jam = (CenterKhz: 42_500.0, BandwidthKhz: 5_000.0); // 40–45 MHz partial band
        var open = Hopset.Sincgars();
        var locked = Hopset.Sincgars([(40_000, 45_000)]);
        Assert.InRange(open.HitFraction([jam]), 0.08, 0.09);
        Assert.Equal(2320 - 201, locked.Count);
        Assert.Equal(0.0, locked.HitFraction([jam]));
    }

    [Fact]
    public void Mu_OverlappingEmissionsCountOnce()
    {
        var hs = Hopset.Sincgars();
        var one = hs.HitFraction([(50_000.0, 2_000.0)]);
        Assert.Equal(one, hs.HitFraction([(50_000.0, 2_000.0), (50_000.0, 2_000.0)]), 9);
    }

    // ------------------------------------------------------------------ comm plan

    private static EccmFill Fill() => new()
    {
        MissionDate = "2026-11-09",
        Mwod =
        [
            new EccmWodEntry { Date = "2026-11-09", Wod = Wod, Label = "D1" },
            new EccmWodEntry { Date = "2026-11-10", Segments = ["300100", "251000", "277775", "312600", "225025", "399975"] },
            new EccmWodEntry { Date = "bogus", Wod = Wod }
        ],
        Sincgars = new EccmSincgarsFill { TransecKey = "BLUE-TEK", Lockouts = [new JammerBand(40_000, 45_000)] }
    };

    [Fact]
    public void CommPlan_CarriesFingerprintsOnly()
    {
        var errors = new List<string>();
        var plan = Fill().ToCommPlan(Now, errors);
        Assert.Equal(2, plan.Mwod.Count);
        Assert.Single(errors);
        Assert.Equal(HaveQuickWod.TryParse(Wod, out _)!.Fingerprint(Day), plan.Mwod[0].Fingerprint);

        var json = JsonSerializer.Serialize(plan, OpenFreqJsonContext.Default.EccmCommPlan);
        Assert.DoesNotContain("300050", json);
        Assert.DoesNotContain("300.050", json);
        Assert.DoesNotContain("BLUE-TEK", json);
        var back = JsonSerializer.Deserialize(json, OpenFreqJsonContext.Default.EccmCommPlan)!;
        Assert.Equal(plan.Mwod[1].Fingerprint, back.Mwod[1].Fingerprint);
        Assert.Equal(plan.Sincgars!.KeyFingerprint, back.Sincgars!.KeyFingerprint);
    }

    [Fact]
    public void CommPlan_DefaultsTodayAndEstimates()
    {
        var plan = new EccmFill().ToCommPlan(Now);
        Assert.Equal("2026-11-09", plan.MissionDate);
        Assert.False(plan.MissionDateFromServer);
        Assert.Equal(100.0, plan.HopRateHz);
        Assert.Equal(2000, plan.NatoChannels);
    }

    // ------------------------------------------------------------------ resolver

    private static EccmSlotSettings Hq(string net = "A12.325") => new() { Mode = HopMode.HaveQuick, Net = net };

    [Fact]
    public void Resolve_PlanWodForTheDayIsReady()
    {
        var plan = Fill().ToCommPlan(Now);
        var r = EccmResolver.Resolve(Hq(), plan, Now);
        Assert.True(r.IsReady, r.Readiness);
        Assert.Equal(plan.Mwod[0].Fingerprint, r.State!.Identity.KeyFingerprint);
        Assert.Equal("A12.325", r.State.Identity.Net);
        Assert.Equal(2000, r.Hopset!.Count);
        Assert.Contains("NATO", r.Description);
    }

    [Fact]
    public void Resolve_ManualWodMatchesThePlan()
    {
        var plan = Fill().ToCommPlan(Now);
        var manual = EccmResolver.Resolve(Hq() with { ManualWod = Wod }, plan, Now);
        var planned = EccmResolver.Resolve(Hq(), plan, Now);
        Assert.Equal(planned.State!.Identity.KeyFingerprint, manual.State!.Identity.KeyFingerprint);
        // and without any plan (today's date is the mission date)
        var noPlan = EccmResolver.Resolve(Hq() with { ManualWod = Wod }, null, Now);
        Assert.Equal(planned.State.Identity.KeyFingerprint, noPlan.State!.Identity.KeyFingerprint);
    }

    [Fact]
    public void Resolve_Readiness()
    {
        var plan = Fill().ToCommPlan(Now);
        Assert.Equal(EccmReadiness.NoWod, EccmResolver.Resolve(Hq(), null, Now).Readiness);
        Assert.Equal(EccmReadiness.NoMwod,
            EccmResolver.Resolve(Hq(), new EccmCommPlan { MissionDate = "2026-12-01", Mwod = plan.Mwod }, Now).Readiness);
        Assert.Equal(EccmReadiness.InvalidNetId, EccmResolver.Resolve(Hq("A12.375"), plan, Now).Readiness);
        Assert.Equal(EccmReadiness.NoTime,
            EccmResolver.Resolve(Hq() with { TodValid = false }, plan, Now).Readiness);
        // the cockpit's own complaint wins
        Assert.Equal("NO TIME", EccmResolver.Resolve(Hq() with { CockpitStatus = "NO TIME" }, plan, Now).Readiness);
        Assert.False(EccmResolver.Resolve(Hq() with { CockpitStatus = "NO TIME" }, plan, Now).State!.Identity.TodValid);
        // a ready cockpit without OpenFreq fill gets the shared default
        var cockpit = EccmResolver.Resolve(Hq() with { CockpitStatus = "", Source = EccmSource.Cockpit }, null, Now);
        Assert.True(cockpit.IsReady);
        Assert.Equal(EccmResolver.DefaultWodFingerprint(Day), cockpit.State!.Identity.KeyFingerprint);
    }

    [Fact]
    public void Resolve_FmtUsesThePlanList()
    {
        var plan = new EccmCommPlan { MissionDate = "2026-11-09", FmtFrequenciesKhz = [250_000, 260_000, 270_000, 280_000, 290_000] };
        var r = EccmResolver.Resolve(Hq("A00.025") with { ManualWod = Wod }, plan, Now);
        Assert.True(r.IsReady);
        Assert.Equal(5, r.Hopset!.Count);
    }

    [Fact]
    public void Resolve_Sincgars()
    {
        var plan = Fill().ToCommPlan(Now);
        var r = EccmResolver.Resolve(new EccmSlotSettings { Mode = HopMode.Sincgars, Net = "1" }, plan, Now);
        Assert.True(r.IsReady);
        Assert.Equal("F001", r.State!.Identity.Net);
        Assert.Equal(plan.Sincgars!.KeyFingerprint, r.State.Identity.KeyFingerprint);
        Assert.Equal(2320 - 201, r.Hopset!.Count);
        Assert.Equal(4000, r.State.SyncWindowMs);

        var manual = EccmResolver.Resolve(new EccmSlotSettings { Mode = HopMode.Sincgars, Net = "F001", ManualSincgarsKey = "BLUE-TEK" }, plan, Now);
        Assert.Equal(r.State.Identity.KeyFingerprint, manual.State!.Identity.KeyFingerprint);

        var noPlan = EccmResolver.Resolve(new EccmSlotSettings { Mode = HopMode.Sincgars, Net = "F002" }, null, Now);
        Assert.True(noPlan.IsReady);
        Assert.Equal(EccmReadiness.NoNet,
            EccmResolver.Resolve(new EccmSlotSettings { Mode = HopMode.Sincgars }, null, Now).Readiness);
    }

    [Fact]
    public void Resolve_OffIsNotHopping() =>
        Assert.False(EccmResolver.Resolve(EccmSlotSettings.Off, null, Now).IsHopping);

    // ------------------------------------------------------------------ TRANSEC gate

    private static HopSlotState Rx(HopNetIdentity id, double window = 50) => new(id, 100, window);

    private static HopNetIdentity Id(string net = "A12.325", string key = "k1", bool tod = true, long off = 0,
        HopMode mode = HopMode.HaveQuick) => new(mode, net, key, tod, off);

    [Fact]
    public void Gate_MatchingNetWodAndTodIsHeard() =>
        Assert.True(TransecGate.CanHear(Rx(Id()), true, Id(), true));

    [Theory]
    [InlineData("A12.350", "k1", true, 0)]     // other net
    [InlineData("A12.325", "k2", true, 0)]     // other WOD
    [InlineData("A12.325", "k1", false, 0)]    // transmitter has no TOD
    [InlineData("A12.325", "k1", true, 51)]    // TOD out of the sync window
    [InlineData("A12.325", "", true, 0)]       // no fill
    public void Gate_MismatchIsNotHeard(string net, string key, bool tod, long offset) =>
        Assert.False(TransecGate.CanHear(Rx(Id()), true, Id(net, key, tod, offset), true));

    [Fact]
    public void Gate_TodInsideTheWindowIsHeard()
    {
        Assert.True(TransecGate.CanHear(Rx(Id()), true, Id(off: -50), true));
        Assert.True(TransecGate.CanHear(Rx(Id(mode: HopMode.Sincgars), 4000), true,
            Id(mode: HopMode.Sincgars, off: 3500), true));
        Assert.False(TransecGate.CanHear(Rx(Id(mode: HopMode.Sincgars), 4000), true, Id(), true)); // mode
    }

    [Fact]
    public void Gate_HoppingAndSingleChannelDoNotMeet()
    {
        Assert.False(TransecGate.CanHear(Rx(Id()), true, null, false));
        Assert.False(TransecGate.CanHear(null, false, Id(), true));
        Assert.False(TransecGate.CanHear(Rx(Id()), true, null, true)); // a legacy HQ flag isn't a net identity
    }

    [Fact]
    public void Gate_LegacyFlagStillMatchesItself()
    {
        Assert.True(TransecGate.CanHear(null, false, null, false));
        Assert.True(TransecGate.CanHear(null, true, null, true));
        Assert.False(TransecGate.CanHear(null, true, null, false));
        // a receiver in "off" mode is single channel
        Assert.True(TransecGate.CanHear(Rx(Id(mode: HopMode.None)), false, null, false));
    }

    [Fact]
    public void Sequence_IsDeterministicAndSpreads()
    {
        var seed = Id().Seed;
        Assert.Equal(seed, Id().Seed);
        Assert.NotEqual(seed, Id(net: "A12.350").Seed);
        var counts = new int[8];
        for (long h = 0; h < 80_000; h++) counts[HopSequence.ChannelIndex(seed, h, 8)]++;
        Assert.All(counts, c => Assert.InRange(c, 9_500, 10_500));
        Assert.Equal(HopSequence.Uniform(seed, 1234, 7), HopSequence.Uniform(seed, 1234, 7));
        Assert.Equal(123_456L, HopSequence.HopIndexAt(1234.567, 100));
    }
}
