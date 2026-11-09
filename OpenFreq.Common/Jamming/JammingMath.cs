using OpenFreqAudio;
using OpenFreqAudio.Jamming;

namespace OpenFreq.Common.Jamming;

/// <summary>Receiver channel class, which sets the IF passband the jammer must land in.</summary>
public enum ReceiverChannelClass
{
    /// <summary>AM, 25 kHz channel (VHF 118–137 / UHF 225–400): ≈16 kHz IF.</summary>
    Am25,

    /// <summary>AM, 8.33 kHz channel: ≈6 kHz IF.</summary>
    Am833,

    /// <summary>FM, 25 kHz channel (30–88 MHz tactical): ≈16 kHz IF.</summary>
    Fm25
}

/// <summary>Modulation family for kill thresholds and intelligibility.</summary>
public enum VoiceModulation
{
    Am,
    Fm,
    Digital
}

/// <summary>
/// Pure jamming math (docs/jamming/research-01-physics.md §1, §6; research-02-eccm.md §6). All powers dBm unless
/// noted, frequencies kHz, distances km. No state, no I/O: every function is unit-tested.
/// </summary>
public static class JammingMath
{
    /// <summary>Guard frequencies protected from barrage by default, kHz.</summary>
    public static readonly IReadOnlyList<int> GuardFrequenciesKhz = [121_500, 243_000];

    /// <summary>Thermal noise density at 290 K, dBm/Hz.</summary>
    public const double ThermalNoiseDbmPerHz = -174.0;

    /// <summary>Jammer-to-signal ratio that denies a link (research-02 §6.6): AM ≈ 0 dB, FM ≈ +3 dB, digital ≈ 0 dB.</summary>
    public static double KillThresholdDb(VoiceModulation modulation) => modulation switch
    {
        VoiceModulation.Fm => 3.0,
        _ => 0.0
    };

    // ------------------------------------------------------------------ units

    public static double WattsToDbm(double watts) => 10.0 * Math.Log10(Math.Max(1e-12, watts) * 1000.0);

    public static double DbmToWatts(double dbm) => Math.Pow(10.0, dbm / 10.0) / 1000.0;

    public static double WattsToDbw(double watts) => 10.0 * Math.Log10(Math.Max(1e-12, watts));

    /// <summary>Power sum of dB values (e.g. noise ⊕ jammers).</summary>
    public static double PowerSumDb(IEnumerable<double> valuesDb)
    {
        var linear = valuesDb.Sum(v => Math.Pow(10.0, v / 10.0));
        return linear > 0 ? 10.0 * Math.Log10(linear) : double.NegativeInfinity;
    }

    // ------------------------------------------------------------------ propagation (research-01 §1.3)

    /// <summary>Free-space path loss, ITU-R P.525: 32.44 + 20 log d[km] + 20 log f[MHz].</summary>
    public static double FreeSpacePathLossDb(double distanceKm, double frequencyMhz) =>
        32.44 + 20.0 * Math.Log10(Math.Max(1e-6, distanceKm)) + 20.0 * Math.Log10(Math.Max(1e-6, frequencyMhz));

    /// <summary>Two-ray (plane-earth) far-field loss: 120 + 40 log d[km] − 20 log hT − 20 log hR.</summary>
    public static double TwoRayPathLossDb(double distanceKm, double txHeightM, double rxHeightM) =>
        120.0 + 40.0 * Math.Log10(Math.Max(1e-6, distanceKm)) - 20.0 * Math.Log10(Math.Max(0.1, txHeightM))
        - 20.0 * Math.Log10(Math.Max(0.1, rxHeightM));

    /// <summary>Fresnel-zone distance (Adamy's form): hT·hR·f / 24 000, km.</summary>
    public static double FresnelZoneKm(double txHeightM, double rxHeightM, double frequencyMhz) =>
        txHeightM * rxHeightM * frequencyMhz / 24_000.0;

    /// <summary>Free space inside the Fresnel-zone distance, two-ray beyond it.</summary>
    public static double FlatEarthPathLossDb(double distanceKm, double frequencyMhz, double txHeightM,
        double rxHeightM) =>
        distanceKm <= FresnelZoneKm(txHeightM, rxHeightM, frequencyMhz)
            ? FreeSpacePathLossDb(distanceKm, frequencyMhz)
            : Math.Max(FreeSpacePathLossDb(distanceKm, frequencyMhz),
                TwoRayPathLossDb(distanceKm, txHeightM, rxHeightM));

    /// <summary>4/3-earth radio horizon, km: 4.12 (√hT + √hR).</summary>
    public static double RadioHorizonKm(double txHeightM, double rxHeightM) =>
        4.12 * (Math.Sqrt(Math.Max(0, txHeightM)) + Math.Sqrt(Math.Max(0, rxHeightM)));

    // ------------------------------------------------------------------ antenna (research-01 §1.4)

    /// <summary>Azimuth gain relative to boresight, dB (≤ 0). Omni: 0. Sector: parabolic main lobe
    /// −12 (θ/θ3)² (−3 dB at ±beamwidth/2), floored at −frontToBack.</summary>
    public static double SectorPatternDb(JammerAntennaPattern pattern, double boresightDeg, double beamwidthDeg,
        double frontToBackDb, double bearingToReceiverDeg)
    {
        if (pattern == JammerAntennaPattern.Omni) return 0.0;
        var off = Math.Abs(NormalizeDeg(bearingToReceiverDeg - boresightDeg));
        var theta3 = Math.Max(1.0, beamwidthDeg);
        var attenuation = 12.0 * (off / theta3) * (off / theta3);
        return -Math.Min(attenuation, Math.Max(0.0, frontToBackDb));
    }

    /// <summary>EIRP toward the receiver, dBm: P + G·pattern − feeder loss.</summary>
    public static double EirpDbm(double txPowerW, double antennaGainDbi, double feederLossDb,
        JammerAntennaPattern pattern = JammerAntennaPattern.Omni, double boresightDeg = 0, double beamwidthDeg = 60,
        double frontToBackDb = 20, double bearingToReceiverDeg = 0) =>
        WattsToDbm(txPowerW) + antennaGainDbi - feederLossDb +
        SectorPatternDb(pattern, boresightDeg, beamwidthDeg, frontToBackDb, bearingToReceiverDeg);

    /// <summary>EIRP of a resolved jammer toward a bearing, including the share of PA power one emission gets.</summary>
    public static double EirpDbm(JammerDefinition resolved, double bearingToReceiverDeg, double powerFraction = 1.0) =>
        EirpDbm((resolved.TxPowerW ?? 0) * Math.Clamp(powerFraction, 0, 1e6), resolved.AntennaGainDbi ?? 0,
            resolved.FeederLossDb ?? 0, resolved.Pattern ?? JammerAntennaPattern.Omni, resolved.AzimuthDeg ?? 0,
            resolved.BeamwidthDeg ?? JammerPresets.DefaultBeamwidthDeg,
            resolved.FrontToBackDb ?? JammerPresets.DefaultFrontToBackDb, bearingToReceiverDeg);

    /// <summary>Fraction of the PA power one emission gets when <paramref name="emissionCount"/> share
    /// <paramref name="powerAmplifiers"/> PAs (research-03 §4: −10 log N when N spots share one PA).</summary>
    public static double PowerFractionPerEmission(int emissionCount, int powerAmplifiers) =>
        emissionCount <= 0 ? 0.0 : Math.Min(1.0, Math.Max(1, powerAmplifiers) / (double)emissionCount);

    /// <summary>Bearing from (x1,y1) to (x2,y2) in a north-up frame where +y is north and +x east, degrees true.</summary>
    public static double BearingDeg(double fromEast, double fromNorth, double toEast, double toNorth) =>
        NormalizeDeg360(Math.Atan2(toEast - fromEast, toNorth - fromNorth) * 180.0 / Math.PI);

    /// <summary>Initial great-circle bearing from point 1 to point 2, degrees true.</summary>
    public static double GeodeticBearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        var p1 = lat1 * Math.PI / 180.0;
        var p2 = lat2 * Math.PI / 180.0;
        var dl = (lon2 - lon1) * Math.PI / 180.0;
        var y = Math.Sin(dl) * Math.Cos(p2);
        var x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
        return NormalizeDeg360(Math.Atan2(y, x) * 180.0 / Math.PI);
    }

    private static double NormalizeDeg(double deg)
    {
        deg %= 360.0;
        if (deg > 180.0) deg -= 360.0;
        if (deg < -180.0) deg += 360.0;
        return deg;
    }

    private static double NormalizeDeg360(double deg) => ((deg % 360.0) + 360.0) % 360.0;

    // ------------------------------------------------------------------ bandwidth (research-01 §1.6)

    /// <summary>The receiver's channel class for a tuned frequency: FM where the band is FM (30–88 MHz),
    /// 8.33 where the frequency is in the VHF air band off the 25 kHz raster, else AM 25 kHz.</summary>
    public static ReceiverChannelClass ClassifyReceiver(int frequencyKhz)
    {
        if (FastPathAudioSim.GetBandConfig(frequencyKhz).Modulation == ModulationType.FM)
            return ReceiverChannelClass.Fm25;
        if (frequencyKhz is >= 118_000 and < 137_000 && frequencyKhz % 25 != 0)
            return ReceiverChannelClass.Am833;
        return ReceiverChannelClass.Am25;
    }

    /// <summary>IF (−6 dB) bandwidth of a receiver class, kHz (research-01 §6.4 receiver presets).</summary>
    public static double ReceiverBandwidthKhz(ReceiverChannelClass channelClass) => channelClass switch
    {
        ReceiverChannelClass.Am833 => 6.0,
        _ => 16.0
    };

    public static VoiceModulation ModulationOf(ReceiverChannelClass channelClass) =>
        channelClass == ReceiverChannelClass.Fm25 ? VoiceModulation.Fm : VoiceModulation.Am;

    /// <summary>Width of the overlap between [c1 ± w1/2] and [c2 ± w2/2] (any consistent unit).</summary>
    public static double OverlapWidth(double center1, double width1, double center2, double width2)
    {
        var lo = Math.Max(center1 - width1 / 2.0, center2 - width2 / 2.0);
        var hi = Math.Min(center1 + width1 / 2.0, center2 + width2 / 2.0);
        return Math.Max(0.0, hi - lo);
    }

    /// <summary>Fraction (0..1) of a flat jammer's power that falls in the receiver passband:
    /// overlap(B_J, B_R) / B_J. A carrier (B_J ≈ 0) is all in-band when its centre is inside the passband.</summary>
    public static double InBandFraction(double jammerCenterKhz, double jammerBandwidthKhz, double receiverCenterKhz,
        double receiverBandwidthKhz)
    {
        if (jammerBandwidthKhz <= 1e-6)
            return Math.Abs(jammerCenterKhz - receiverCenterKhz) < receiverBandwidthKhz / 2.0 ? 1.0 : 0.0;
        var overlap = OverlapWidth(jammerCenterKhz, jammerBandwidthKhz, receiverCenterKhz, receiverBandwidthKhz);
        return Math.Clamp(overlap / jammerBandwidthKhz, 0.0, 1.0);
    }

    /// <summary>In-band loss in dB (≥ 0; +∞ when nothing falls in the passband).</summary>
    public static double InBandLossDb(double jammerCenterKhz, double jammerBandwidthKhz, double receiverCenterKhz,
        double receiverBandwidthKhz)
    {
        var fraction = InBandFraction(jammerCenterKhz, jammerBandwidthKhz, receiverCenterKhz, receiverBandwidthKhz);
        return fraction <= 0 ? double.PositiveInfinity : -10.0 * Math.Log10(fraction);
    }

    /// <summary>Barrage dilution for a fully overlapping jammer: 10 log10(max(1, B_J / B_R)).</summary>
    public static double BarrageDilutionDb(double jammerBandwidthKhz, double receiverBandwidthKhz) =>
        10.0 * Math.Log10(Math.Max(1.0, jammerBandwidthKhz / Math.Max(1e-9, receiverBandwidthKhz)));

    /// <summary>Centre of the part of the jammer that falls in the passband, relative to the tuned frequency,
    /// kHz. For a spot inside the passband, its own offset.</summary>
    public static double InBandCenterOffsetKhz(double jammerCenterKhz, double jammerBandwidthKhz,
        double receiverCenterKhz, double receiverBandwidthKhz)
    {
        var lo = Math.Max(jammerCenterKhz - jammerBandwidthKhz / 2.0, receiverCenterKhz - receiverBandwidthKhz / 2.0);
        var hi = Math.Min(jammerCenterKhz + jammerBandwidthKhz / 2.0, receiverCenterKhz + receiverBandwidthKhz / 2.0);
        if (hi < lo) return jammerCenterKhz - receiverCenterKhz;
        return (lo + hi) / 2.0 - receiverCenterKhz;
    }

    // ------------------------------------------------------------------ noise, J/N, J/S

    /// <summary>Thermal noise floor: −174 + 10 log B[Hz] + NF, dBm.</summary>
    public static double ThermalNoiseFloorDbm(double bandwidthHz, double noiseFigureDb) =>
        ThermalNoiseDbmPerHz + 10.0 * Math.Log10(Math.Max(1.0, bandwidthHz)) + noiseFigureDb;

    /// <summary>Jammer power arriving at the receiver port, dBm: EIRP − L_path + G_R − L_pol.</summary>
    public static double ReceivedPowerDbm(double eirpDbm, double pathLossDb, double receiverGainDbi = 0,
        double polarizationLossDb = 0) => eirpDbm - pathLossDb + receiverGainDbi - polarizationLossDb;

    /// <summary>In-passband jammer-to-noise ratio, dB: J − in-band loss − N.</summary>
    public static double Jnr(double receivedJammerDbm, double inBandLossDb, double noiseFloorDbm) =>
        receivedJammerDbm - inBandLossDb - noiseFloorDbm;

    /// <summary>Free-space J/S (research-01 eq. 2): ERP_J − ERP_T − 20 log d_J + 20 log d_S + G_RJ − G_RS − BW.</summary>
    public static double FreeSpaceJsDb(double jammerEirpDbm, double jammerRangeKm, double signalEirpDbm,
        double signalRangeKm, double receiverGainTowardJammerDbi = 0, double receiverGainTowardSignalDbi = 0,
        double bandwidthLossDb = 0) =>
        jammerEirpDbm - signalEirpDbm - 20.0 * Math.Log10(jammerRangeKm) + 20.0 * Math.Log10(signalRangeKm) +
        receiverGainTowardJammerDbi - receiverGainTowardSignalDbi - bandwidthLossDb;

    /// <summary>Free-space burn-through range (research-01 eq. 4): the talker range at which J/S drops to
    /// <paramref name="requiredJsDb"/>, km. bandwidthLossDb is the jammer's in-band loss (≥ 0).</summary>
    public static double BurnThroughRangeKm(double jammerRangeKm, double requiredJsDb, double jammerEirpDbm,
        double signalEirpDbm, double receiverGainTowardJammerDbi = 0, double receiverGainTowardSignalDbi = 0,
        double bandwidthLossDb = 0) =>
        jammerRangeKm * Math.Pow(10.0, (requiredJsDb - jammerEirpDbm + signalEirpDbm -
                                        receiverGainTowardJammerDbi + receiverGainTowardSignalDbi +
                                        bandwidthLossDb) / 20.0);

    // ------------------------------------------------------------------ hopping (research-02 §6.2–6.3)

    /// <summary>Follower hit fraction per dwell: max(0, 1 − T_delay · R_h).</summary>
    public static double FollowerHitFraction(double reactionDelayMs, double hopRateHz) =>
        Math.Clamp(1.0 - reactionDelayMs / 1000.0 * hopRateHz, 0.0, 1.0);

    /// <summary>Fraction μ of a hopset's N channels covered by the jammed bands (kHz edges):
    /// |hopset ∩ jammed| / N, computed analytically from the covered share of the hopset span. A jammer that
    /// touches the hopset at all hits at least one channel (1/N), e.g. a spot jammer on an HQ net.</summary>
    public static double HopFraction(IEnumerable<(double StartKhz, double EndKhz)> jammedBands, double hopsetStartKhz,
        double hopsetEndKhz, int channelCount)
    {
        if (channelCount <= 0 || hopsetEndKhz <= hopsetStartKhz) return 0.0;

        var clipped = jammedBands
            .Select(b => (Lo: Math.Max(Math.Min(b.StartKhz, b.EndKhz), hopsetStartKhz),
                Hi: Math.Min(Math.Max(b.StartKhz, b.EndKhz), hopsetEndKhz)))
            .Where(b => b.Hi > b.Lo)
            .OrderBy(b => b.Lo)
            .ToList();
        if (clipped.Count == 0) return 0.0;

        // merge overlaps so two jammers on the same sub-band don't count twice
        double covered = 0, curLo = clipped[0].Lo, curHi = clipped[0].Hi;
        foreach (var (lo, hi) in clipped.Skip(1))
        {
            if (lo <= curHi)
            {
                curHi = Math.Max(curHi, hi);
                continue;
            }

            covered += curHi - curLo;
            (curLo, curHi) = (lo, hi);
        }

        covered += curHi - curLo;

        var mu = covered / (hopsetEndKhz - hopsetStartKhz);
        return Math.Clamp(Math.Max(mu, 1.0 / channelCount), 0.0, 1.0);
    }

    /// <summary>Word intelligibility of hopping voice with a fraction μ of hops lost (research-02 §6.4 table,
    /// linear interpolation).</summary>
    public static double WordIntelligibilityFromHopLoss(double mu)
    {
        ReadOnlySpan<double> x = [0.0, 0.1, 0.25, 0.5, 0.75, 0.9, 1.0];
        ReadOnlySpan<double> y = [1.0, 0.97, 0.92, 0.80, 0.45, 0.15, 0.0];
        mu = Math.Clamp(mu, 0.0, 1.0);
        for (var i = 1; i < x.Length; i++)
        {
            if (mu <= x[i])
                return y[i - 1] + (y[i] - y[i - 1]) * (mu - x[i - 1]) / (x[i] - x[i - 1]);
        }

        return 0.0;
    }

    // ------------------------------------------------------------------ intelligibility (research-01 §6.3–6.4)

    /// <summary>SII of a clean 300–3000 Hz radio channel.</summary>
    public const double SiiMax = 0.81;

    /// <summary>Minimally acceptable SII (MIL-STD-1472): a message gets through at or above this.</summary>
    public const double SiiMinimallyAcceptable = 0.3;

    /// <summary>SII from audio SNR with flat in-band noise: SII_max · clip((SNR + 15) / 30, 0, 1).</summary>
    public static double SiiFromAudioSnr(double audioSnrDb, double siiMax = SiiMax) =>
        siiMax * Math.Clamp((audioSnrDb + 15.0) / 30.0, 0.0, 1.0);

    /// <summary>Masking-efficiency correction M_tech (dB, intelligibility scalar only), research-01 §6.4.</summary>
    public static double MaskingCorrectionDb(JammerTechnique technique, VoiceModulation modulation) => technique switch
    {
        // A tone masks only its critical band; on FM the capture test (EstimateSii) handles the strong case.
        JammerTechnique.Tone or JammerTechnique.MultiTone => -8.0,
        JammerTechnique.SteppedTones or JammerTechnique.Swept or JammerTechnique.Gulls => -4.0,
        JammerTechnique.Recorded => 2.0,
        _ => 0.0
    };

    /// <summary>Audio SNR after detection from predetection SINR (research-01 §6.3).
    /// AM: SINR − 5, minus 0.5·(6 − SINR) below 6 dB. FM: captured → −30; ≥ 10 dB → SINR + 4;
    /// below threshold SINR + 4 − 2.5·(10 − SINR).</summary>
    public static double AudioSnrFromSinr(double sinrDb, VoiceModulation modulation, bool fmCaptured = false)
    {
        switch (modulation)
        {
            case VoiceModulation.Fm:
                if (fmCaptured) return -30.0;
                return sinrDb >= 10.0 ? sinrDb + 4.0 : sinrDb + 4.0 - 2.5 * (10.0 - sinrDb);
            default:
                var snr = sinrDb - 5.0;
                if (sinrDb < 6.0) snr -= 0.5 * (6.0 - sinrDb);
                return snr;
        }
    }

    /// <summary>One jammer as seen by the intelligibility scalar.</summary>
    public readonly record struct JammerAtReceiver(double InBandDbm, JammerTechnique Technique, double Duty = 1.0);

    /// <summary>Scalar SII for a wanted signal under noise and jammers (research-01 §6.3).</summary>
    public static double EstimateSii(double signalDbm, double noiseDbm, IEnumerable<JammerAtReceiver> jammers,
        VoiceModulation modulation)
    {
        var list = jammers.ToList();
        var effective = list.Select(j =>
            j.InBandDbm + MaskingCorrectionDb(j.Technique, modulation) +
            10.0 * Math.Log10(Math.Clamp(j.Duty, 1e-6, 1.0)));
        var sinr = signalDbm - PowerSumDb(effective.Append(noiseDbm));

        var captured = modulation == VoiceModulation.Fm && list.Any(j =>
            j.Technique != JammerTechnique.Noise && j.InBandDbm > signalDbm + KillThresholdDb(VoiceModulation.Fm));
        return SiiFromAudioSnr(AudioSnrFromSinr(sinr, modulation, captured));
    }

    // ------------------------------------------------------------------ misc

    /// <summary>Stable 32-bit FNV-1a hash of a jammer id, for seeding waveforms and the carrier offset.</summary>
    public static int StableSeed(string id)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in id)
            {
                hash ^= c;
                hash *= 16777619u;
            }

            return (int)hash;
        }
    }

    /// <summary>The jammer's fixed transmitter offset within ± tolerance, Hz, seeded by its id.</summary>
    public static double CarrierToleranceOffsetHz(string id, double toleranceHz)
    {
        if (toleranceHz <= 0) return 0.0;
        var unit = (uint)StableSeed(id) / (double)uint.MaxValue; // 0..1
        return (unit * 2.0 - 1.0) * toleranceHz;
    }
}
