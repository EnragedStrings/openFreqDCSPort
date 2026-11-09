using OpenFreq.Common.Jamming;
using OpenFreqAudio.Jamming;

namespace OpenFreq.Common.Tests.Jamming;

/// <summary>
/// JammingMath, unit by unit, plus the design doc's acceptance checks (docs/jamming/JAMMING_DESIGN.md
/// "Acceptance sanity checks") at the math level, using the same assumptions as the research: free space, all
/// receive gains 0 dBi, 16 kHz AM IF, thermal noise −174 + 10 log B + 8 dB NF.
/// </summary>
public class JammingMathTests
{
    private const double UhfMhz = 300.0;
    private const double Nm = 1.852;
    private static readonly double RxBwKhz = JammingMath.ReceiverBandwidthKhz(ReceiverChannelClass.Am25);
    private static readonly double ThermalFloorDbm = JammingMath.ThermalNoiseFloorDbm(16_000, 8);

    // ------------------------------------------------------------------ basics

    [Fact]
    public void FreeSpacePathLoss_MatchesResearchWorkedExample()
    {
        // research-01 §1.8: 100 km at 250 MHz -> 32.44 + 40 + 47.96 = 120.4 dB
        Assert.Equal(120.4, JammingMath.FreeSpacePathLossDb(100, 250), 1);
    }

    [Fact]
    public void ThermalNoiseFloor_16kHz_Nf8_IsMinus124()
    {
        Assert.Equal(-124.0, ThermalFloorDbm, 0);
    }

    [Fact]
    public void WattsDbm_RoundTrip()
    {
        Assert.Equal(60.0, JammingMath.WattsToDbm(1000), 6);
        Assert.Equal(1000.0, JammingMath.DbmToWatts(60), 6);
        Assert.Equal(30.0, JammingMath.WattsToDbw(1000), 6);
    }

    [Fact]
    public void PowerSum_TwoEqualPowers_AddsThreeDb()
    {
        Assert.Equal(3.01, JammingMath.PowerSumDb([-100, -100]) + 100, 2);
    }

    [Fact]
    public void TwoRay_And_Fresnel_And_Horizon_MatchResearchForms()
    {
        // ground jammer -> ground FM radio, 10 m / 2 m, 50 MHz: FZ = 0.04 km, so 10 km is two-ray (40 log)
        Assert.Equal(0.0417, JammingMath.FresnelZoneKm(10, 2, 50), 3);
        var twoRay = JammingMath.TwoRayPathLossDb(10, 10, 2);
        Assert.Equal(120 + 40 - 20 - 6.02, twoRay, 1);
        Assert.Equal(twoRay, JammingMath.FlatEarthPathLossDb(10, 50, 10, 2), 6);
        // ground jammer -> FL100 at 100 km is inside the Fresnel distance: free space
        Assert.Equal(JammingMath.FreeSpacePathLossDb(100, 250), JammingMath.FlatEarthPathLossDb(100, 250, 10, 3000), 6);
        // research-01 §1.3: 10 m mast vs 3000 m -> ≈239 km
        Assert.Equal(239, JammingMath.RadioHorizonKm(10, 3000), 0);
    }

    [Fact]
    public void Eirp_PresetR934b_Is33dBW()
    {
        var r934b = JammerPresets.Resolve(new JammerDefinition { Id = "j", PresetId = "r934b" });
        Assert.Equal(63.0, JammingMath.EirpDbm(r934b, 0), 1);
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(30, -3.0)] // edge of a 60° beam
    [InlineData(-30, -3.0)]
    [InlineData(90, -20.0)] // floored at front-to-back
    [InlineData(180, -20.0)]
    [InlineData(350, -12.0 * (10.0 / 60) * (10.0 / 60))] // wraps around north
    public void SectorPattern_ParabolicMainLobe_FlooredAtFrontToBack(double bearing, double expectedDb)
    {
        Assert.Equal(expectedDb, JammingMath.SectorPatternDb(JammerAntennaPattern.Sector, 0, 60, 20, bearing), 3);
        Assert.Equal(0.0, JammingMath.SectorPatternDb(JammerAntennaPattern.Omni, 0, 60, 20, bearing));
    }

    [Fact]
    public void Eirp_SectorAwayFromReceiver_LosesFrontToBack()
    {
        var on = JammingMath.EirpDbm(500, 8, 2, JammerAntennaPattern.Sector, 90, 60, 20, 90);
        var off = JammingMath.EirpDbm(500, 8, 2, JammerAntennaPattern.Sector, 90, 60, 20, 270);
        Assert.Equal(20.0, on - off, 6);
    }

    [Theory]
    [InlineData(1, 3, 1.0)]
    [InlineData(3, 3, 1.0)]
    [InlineData(6, 3, 0.5)]
    [InlineData(4, 1, 0.25)]
    [InlineData(0, 1, 0.0)]
    public void PowerFractionPerEmission_SharesPas(int emissions, int pas, double expected)
    {
        Assert.Equal(expected, JammingMath.PowerFractionPerEmission(emissions, pas), 6);
    }

    [Fact]
    public void Bearings_NorthUpAndGeodetic()
    {
        Assert.Equal(90, JammingMath.BearingDeg(0, 0, 10, 0), 6);
        Assert.Equal(0, JammingMath.BearingDeg(0, 0, 0, 10), 6);
        Assert.Equal(270, JammingMath.BearingDeg(0, 0, -10, 0), 6);
        Assert.Equal(90, JammingMath.GeodeticBearingDeg(0, 0, 0, 1), 3);
        Assert.Equal(0, JammingMath.GeodeticBearingDeg(40, 40, 41, 40), 3);
    }

    // ------------------------------------------------------------------ bandwidth

    [Theory]
    [InlineData(251_000, ReceiverChannelClass.Am25)]
    [InlineData(124_000, ReceiverChannelClass.Am25)]
    [InlineData(124_008, ReceiverChannelClass.Am833)]
    [InlineData(118_017, ReceiverChannelClass.Am833)]
    [InlineData(40_000, ReceiverChannelClass.Fm25)]
    public void ClassifyReceiver_ByBandAndRaster(int khz, ReceiverChannelClass expected)
    {
        Assert.Equal(expected, JammingMath.ClassifyReceiver(khz));
    }

    [Fact]
    public void ReceiverBandwidths_FromResearchTable()
    {
        Assert.Equal(16.0, JammingMath.ReceiverBandwidthKhz(ReceiverChannelClass.Am25));
        Assert.Equal(6.0, JammingMath.ReceiverBandwidthKhz(ReceiverChannelClass.Am833));
        Assert.Equal(16.0, JammingMath.ReceiverBandwidthKhz(ReceiverChannelClass.Fm25));
        Assert.Equal(VoiceModulation.Fm, JammingMath.ModulationOf(ReceiverChannelClass.Fm25));
        Assert.Equal(VoiceModulation.Am, JammingMath.ModulationOf(ReceiverChannelClass.Am833));
    }

    [Fact]
    public void InBandFraction_SpotCentred_IsReceiverOverJammerWidth()
    {
        Assert.Equal(16.0 / 25.0, JammingMath.InBandFraction(251_000, 25, 251_000, 16), 6);
        Assert.Equal(1.0, JammingMath.InBandFraction(251_000, 10, 251_000, 16), 6);
        // 8.33 receiver under a 25 kHz spot loses more
        Assert.Equal(6.0 / 25.0, JammingMath.InBandFraction(124_008, 25, 124_008, 6), 6);
    }

    [Fact]
    public void InBandFraction_AdjacentChannel_IsZero_AndHalfOverlapIsPartial()
    {
        Assert.Equal(0.0, JammingMath.InBandFraction(251_025, 25, 251_000, 16));
        Assert.True(double.IsPositiveInfinity(JammingMath.InBandLossDb(251_025, 25, 251_000, 16)));
        // 25 kHz spot offset by 12.5 kHz: overlap [-8, 8] ∩ [0, 25] = 8 kHz
        Assert.Equal(8.0 / 25.0, JammingMath.InBandFraction(251_012.5, 25, 251_000, 16), 6);
        Assert.Equal(4.0, JammingMath.InBandCenterOffsetKhz(251_012.5, 25, 251_000, 16), 6);
    }

    [Fact]
    public void InBandFraction_Carrier_InsideOrOutsidePassband()
    {
        Assert.Equal(1.0, JammingMath.InBandFraction(251_001, 0, 251_000, 16));
        Assert.Equal(0.0, JammingMath.InBandFraction(251_010, 0, 251_000, 16));
    }

    [Fact]
    public void BarrageDilution_225to400_Over16kHz_Is40dB()
    {
        // research-01 §1.6: 60 − 10 log(175e6/16e3) = 19.6 dBm effective per channel
        Assert.Equal(40.4, JammingMath.BarrageDilutionDb(175_000, 16), 1);
        Assert.Equal(0.0, JammingMath.BarrageDilutionDb(10, 16));
        Assert.Equal(JammingMath.BarrageDilutionDb(175_000, 16),
            JammingMath.InBandLossDb(312_500, 175_000, 300_000, 16), 6);
    }

    // ------------------------------------------------------------------ J/S, burn-through

    [Fact]
    public void BurnThrough_MatchesResearchWorkedExample()
    {
        // research-01 §1.8: 100 km · 10^((3 − 67 + 40)/20) = 6.3 km
        Assert.Equal(6.3, JammingMath.BurnThroughRangeKm(100, 3, 67, 40), 1);
    }

    [Fact]
    public void BurnThrough_IsWhereJsEqualsRequired()
    {
        var d = JammingMath.BurnThroughRangeKm(80, 0, 63, 40, bandwidthLossDb: 1.94);
        Assert.Equal(0.0, JammingMath.FreeSpaceJsDb(63, 80, 40, d, bandwidthLossDb: 1.94), 6);
    }

    [Fact]
    public void Jnr_IsJMinusLossMinusNoise()
    {
        Assert.Equal(10.0, JammingMath.Jnr(-100, 4, -114), 6);
        Assert.Equal(-53.4, JammingMath.ReceivedPowerDbm(67, 120.4), 6);
    }

    // ------------------------------------------------------------------ hopping

    [Fact]
    public void FollowerHitFraction_2msAt100Hops_Is80Percent()
    {
        Assert.Equal(0.8, JammingMath.FollowerHitFraction(2, 100), 6);
        Assert.Equal(0.0, JammingMath.FollowerHitFraction(20, 100), 6);
        Assert.Equal(1.0, JammingMath.FollowerHitFraction(0, 100), 6);
    }

    [Fact]
    public void HopFraction_MergesOverlapsAndClipsToHopset()
    {
        var mu = JammingMath.HopFraction([(225_000, 260_000), (250_000, 260_000), (100_000, 110_000)],
            225_000, 400_000, 2000);
        Assert.Equal(35_000.0 / 175_000.0, mu, 6);
        Assert.Equal(0.0, JammingMath.HopFraction([(100_000, 110_000)], 225_000, 400_000, 2000));
    }

    [Fact]
    public void WordIntelligibility_InterpolatesHopLossTable()
    {
        Assert.Equal(1.0, JammingMath.WordIntelligibilityFromHopLoss(0), 6);
        Assert.Equal(0.97, JammingMath.WordIntelligibilityFromHopLoss(0.1), 6);
        Assert.Equal(0.86, JammingMath.WordIntelligibilityFromHopLoss(0.375), 6);
        Assert.Equal(0.15, JammingMath.WordIntelligibilityFromHopLoss(0.9), 6);
        Assert.Equal(0.0, JammingMath.WordIntelligibilityFromHopLoss(1.0), 6);
    }

    // ------------------------------------------------------------------ intelligibility

    [Theory]
    [InlineData(-15, 0.0)]
    [InlineData(-10, 0.135)]
    [InlineData(0, 0.405)]
    [InlineData(15, 0.81)]
    [InlineData(30, 0.81)]
    public void Sii_FlatNoiseTable(double snr, double expected)
    {
        Assert.Equal(expected, JammingMath.SiiFromAudioSnr(snr), 3);
    }

    [Fact]
    public void AudioSnr_AmAndFmRules()
    {
        Assert.Equal(15.0, JammingMath.AudioSnrFromSinr(20, VoiceModulation.Am), 6);
        Assert.Equal(-5.0 - 3.0, JammingMath.AudioSnrFromSinr(0, VoiceModulation.Am), 6);
        Assert.Equal(24.0, JammingMath.AudioSnrFromSinr(20, VoiceModulation.Fm), 6);
        Assert.Equal(4.0 - 25.0, JammingMath.AudioSnrFromSinr(0, VoiceModulation.Fm), 6);
        Assert.Equal(-30.0, JammingMath.AudioSnrFromSinr(20, VoiceModulation.Fm, fmCaptured: true), 6);
    }

    [Fact]
    public void Sii_FmCarrierJammerAboveCaptureRatio_Captures()
    {
        var sii = JammingMath.EstimateSii(-80, -124, [new(-76, JammerTechnique.Tone)], VoiceModulation.Fm);
        Assert.Equal(0.0, sii, 6);
        var noise = JammingMath.EstimateSii(-80, -124, [new(-95, JammerTechnique.Noise)], VoiceModulation.Fm);
        Assert.True(noise > 0.7);
    }

    [Fact]
    public void KillThresholds_FromResearch()
    {
        Assert.Equal(0.0, JammingMath.KillThresholdDb(VoiceModulation.Am));
        Assert.Equal(3.0, JammingMath.KillThresholdDb(VoiceModulation.Fm));
        Assert.Equal(0.0, JammingMath.KillThresholdDb(VoiceModulation.Digital));
    }

    [Fact]
    public void CarrierToleranceOffset_IsStableAndBounded()
    {
        var a = JammingMath.CarrierToleranceOffsetHz("sam-site-1", 300);
        Assert.Equal(a, JammingMath.CarrierToleranceOffsetHz("sam-site-1", 300));
        Assert.InRange(a, -300, 300);
        Assert.NotEqual(a, JammingMath.CarrierToleranceOffsetHz("sam-site-2", 300));
        Assert.Equal(0.0, JammingMath.CarrierToleranceOffsetHz("x", 0));
    }

    // ------------------------------------------------------------------ acceptance checks

    /// <summary>J/S at an AM receiver between a jammer and a talker, both free space at 300 MHz, with the
    /// jammer emission's in-band loss.</summary>
    private static double Js(double jammerEirpDbm, double jammerKm, double jammerCenterKhz, double jammerBwKhz,
        double talkerEirpDbm, double talkerKm, int tunedKhz = 300_000)
    {
        var j = JammingMath.ReceivedPowerDbm(jammerEirpDbm, JammingMath.FreeSpacePathLossDb(jammerKm, UhfMhz)) -
                JammingMath.InBandLossDb(jammerCenterKhz, jammerBwKhz, tunedKhz, RxBwKhz);
        var s = JammingMath.ReceivedPowerDbm(talkerEirpDbm, JammingMath.FreeSpacePathLossDb(talkerKm, UhfMhz));
        return j - s;
    }

    [Fact]
    public void Acceptance1_Spot50W_At50nm_DeniesSingleChannelTalkerAt100nm_ButNotHaveQuick()
    {
        var eirp = JammingMath.EirpDbm(50, 0, 0); // 47 dBm
        var talker = JammingMath.WattsToDbm(10); // 10 W radio, 0 dBi
        var js = Js(eirp, 50 * Nm, 300_000, 25, talker, 100 * Nm);

        Assert.InRange(js, 10, 12); // 7 dB power + 6 dB range − 1.9 dB spot spill
        Assert.True(js >= JammingMath.KillThresholdDb(VoiceModulation.Am));

        var s = talker - JammingMath.FreeSpacePathLossDb(100 * Nm, UhfMhz);
        var sii = JammingMath.EstimateSii(s, ThermalFloorDbm,
            [new(s + js, JammerTechnique.Noise)], VoiceModulation.Am);
        Assert.True(sii < 0.15, $"SII {sii}");

        // HQ, NATO hopset N = 2000: one channel of 2000 hit -> 0.05 %
        var mu = JammingMath.HopFraction([(299_987.5, 300_012.5)], 225_000, 400_000, 2000);
        Assert.Equal(0.0005, mu, 6);
        Assert.True(JammingMath.WordIntelligibilityFromHopLoss(mu) > 0.99);
    }

    [Fact]
    public void Acceptance2_Barrage225to400_1kW_Is38to40dBWeakerPerChannel_KillsOnlyClose_HqGainsNothing()
    {
        var spotLoss = JammingMath.InBandLossDb(300_000, 25, 300_000, 25);
        var barrageLoss = JammingMath.InBandLossDb(312_500, 175_000, 300_000, 25);
        Assert.InRange(barrageLoss - spotLoss, 38, 40);
        // ≈ 0.14 W of the 1 kW lands in each 25 kHz channel
        Assert.Equal(0.143, 1000 * Math.Pow(10, -barrageLoss / 10), 3);

        var eirp = JammingMath.EirpDbm(1000, 2, 0);
        var talker = JammingMath.WattsToDbm(10);

        // receiver 5 km from the barrage jammer, talker at 100 km: killed
        Assert.True(Js(eirp, 5, 312_500, 175_000, talker, 100) > 0);

        // receiver 100 km from it, talker 100 km: degraded but readable
        var js = Js(eirp, 100, 312_500, 175_000, talker, 100);
        Assert.InRange(js, -25, -10);
        var s = talker - JammingMath.FreeSpacePathLossDb(100, UhfMhz);
        var clean = JammingMath.EstimateSii(s, ThermalFloorDbm, [], VoiceModulation.Am);
        var jammed = JammingMath.EstimateSii(s, ThermalFloorDbm, [new(s + js, JammerTechnique.Noise)],
            VoiceModulation.Am);
        Assert.True(jammed < clean);
        Assert.True(jammed >= JammingMath.SiiMinimallyAcceptable);

        // HQ: the barrage covers the whole hopset, so every hop sees the same J
        Assert.Equal(1.0, JammingMath.HopFraction([(225_000, 400_000)], 225_000, 400_000, 2000), 6);
    }

    [Fact]
    public void Acceptance3_PartialBand10Percent_HqMuPointOne_SingleChannelInsideKilled_OutsideClear()
    {
        var band = (Start: 225_000.0, End: 242_500.0);
        var mu = JammingMath.HopFraction([band], 225_000, 400_000, 2000);
        Assert.Equal(0.1, mu, 6);
        Assert.Equal(0.97, JammingMath.WordIntelligibilityFromHopLoss(mu), 6); // occasional ticks

        var center = (band.Start + band.End) / 2;
        var eirp = JammingMath.EirpDbm(1000, 2, 0);
        var talker = JammingMath.WattsToDbm(10);
        Assert.True(Js(eirp, 20, center, band.End - band.Start, talker, 100, tunedKhz: 230_000) > 0);
        Assert.Equal(0.0, JammingMath.InBandFraction(center, band.End - band.Start, 250_000, RxBwKhz));
    }

    [Fact]
    public void Acceptance4_Follower2msAgainstHq100Hops_HeavilyDegraded()
    {
        var hit = JammingMath.FollowerHitFraction(2, 100);
        Assert.Equal(0.8, hit, 6);
        Assert.True(JammingMath.WordIntelligibilityFromHopLoss(hit) < 0.45);
    }

    [Fact]
    public void Acceptance6_R934b_DeniesGciAndAwacs_WingmanAt5kmGetsThrough()
    {
        // research-03 §5, same assumptions: free space, receive gains equal, co-channel (frequency cancels)
        var r934b = JammerPresets.Resolve(new JammerDefinition { Id = "r934b", PresetId = "r934b" });
        var jammer = JammingMath.EirpDbm(r934b, 0); // 33 dBW
        var gci = JammingMath.WattsToDbm(100) + 3; // 23 dBW
        var awacs = JammingMath.WattsToDbm(100); // 20 dBW
        var wingman = JammingMath.WattsToDbm(10); // 10 dBW

        var jsGci = JammingMath.FreeSpaceJsDb(jammer, 150, gci, 150);
        var jsAwacs = JammingMath.FreeSpaceJsDb(jammer, 100, awacs, 200);
        var jsWingman = JammingMath.FreeSpaceJsDb(jammer, 150, wingman, 5);

        Assert.InRange(jsGci, 9.5, 19.5);
        Assert.InRange(jsAwacs, 9.5, 19.5);
        Assert.InRange(jsWingman, -7, -5.5);
        Assert.True(jsGci >= JammingMath.KillThresholdDb(VoiceModulation.Am));
        Assert.True(jsWingman < JammingMath.KillThresholdDb(VoiceModulation.Am));

        // the same through the full path budget at 250 MHz: frequency cancels
        var viaPath = Js(jammer, 100, 300_000, 0, awacs, 200);
        Assert.Equal(jsAwacs, viaPath, 6);
    }
}
