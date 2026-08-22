using System;

namespace OpenFreq.Common.Satcom;

/// <summary>
/// SATCOM link-budget math: free-space path loss, carrier/noise power, C/N0, Eb/N0, and
/// modulation-specific BER. PUBLIC STANDARD formulas (FSPL, Friis-style link budget, Boltzmann
/// noise density, C/N0 combining, textbook modulation BER curves) throughout; the two genuinely
/// invented numbers are <see cref="SatcomNetDefinition.SatelliteEirpAdjustDb"/> (no public
/// satellite EIRP/G-T data) and the choice of representative modulation family (no public
/// circuit-level MIL-STD-188-181/183 detail) -- both CALIBRATED_APPROXIMATION and called out where
/// used. See docs/SATCOM_SIMULATION.md.
/// </summary>
public static class SatcomLinkBudget
{
    private const double SpeedOfLightMps = 299_792_458.0; // SOURCE_EXACT

    /// <summary>10*log10(Boltzmann's constant), dBW/K/Hz. PHYSICAL_CALCULATION.</summary>
    private const double BoltzmannDbwPerKPerHz = -228.5991672; // 10*log10(1.380649e-23)

    /// <summary>Free-space path loss in dB: FSPL = 20log10(d) + 20log10(f) + 20log10(4pi/c).
    /// PHYSICAL_CALCULATION.</summary>
    public static double FreeSpacePathLossDb(double distanceMeters, double frequencyHz)
    {
        var d = Math.Max(1.0, distanceMeters);
        var f = Math.Max(1.0, frequencyHz);
        return 20.0 * Math.Log10(d) + 20.0 * Math.Log10(f) + 20.0 * Math.Log10(4.0 * Math.PI / SpeedOfLightMps);
    }

    /// <summary>Received carrier power, dBW. PHYSICAL_CALCULATION given its inputs.</summary>
    public static double ReceivedCarrierDbw(double txPowerDbw, double txAntennaGainDb, double pathLossDb,
        double rxAntennaGainDb, double implementationLossDb) =>
        txPowerDbw + txAntennaGainDb - pathLossDb + rxAntennaGainDb - implementationLossDb;

    /// <summary>Thermal noise power spectral density, dBW/Hz, from receiver system noise
    /// temperature (Kelvin). PHYSICAL_CALCULATION given the noise temperature input (which is
    /// itself CALIBRATED_APPROXIMATION -- see SatcomNetDefinition.SystemNoiseTemperatureK).</summary>
    public static double NoiseDensityDbwPerHz(double systemNoiseTemperatureK) =>
        BoltzmannDbwPerKPerHz + 10.0 * Math.Log10(Math.Max(1.0, systemNoiseTemperatureK));

    /// <summary>Carrier-to-noise-density ratio, dB-Hz. PHYSICAL_CALCULATION.</summary>
    public static double CarrierToNoiseDensityDbHz(double receivedCarrierDbw, double noiseDensityDbwPerHz) =>
        receivedCarrierDbw - noiseDensityDbwPerHz;

    /// <summary>
    /// Bent-pipe transponder combining rule: the end-to-end C/N0 of a relayed link is governed by
    /// both legs together in the LINEAR domain, not an average or soft-min of dB margins:
    /// 1/(C/No)_total = 1/(C/No)_up + 1/(C/No)_down. SOURCE_DERIVED (standard satellite-
    /// communications link-budget combining rule, e.g. Pratt/Bostian/Allnutt "Satellite
    /// Communications"). An excellent uplink cannot rescue a poor downlink and vice versa, and two
    /// simultaneously-marginal legs compound into something worse than either alone, exactly as a
    /// real cascaded RF link does.
    /// </summary>
    public static double CombineCarrierToNoiseDensityDbHz(double uplinkDbHz, double downlinkDbHz)
    {
        var upLinear = Math.Pow(10.0, uplinkDbHz / 10.0);
        var downLinear = Math.Pow(10.0, downlinkDbHz / 10.0);
        if (upLinear <= 0.0 || downLinear <= 0.0) return -999.0;

        var combinedLinear = 1.0 / (1.0 / upLinear + 1.0 / downLinear);
        return 10.0 * Math.Log10(combinedLinear);
    }

    /// <summary>Energy-per-bit to noise-density ratio, dB, from C/N0 and information bit rate.
    /// PHYSICAL_CALCULATION.</summary>
    public static double EbN0Db(double cn0DbHz, double bitRateBps) =>
        cn0DbHz - 10.0 * Math.Log10(Math.Max(1.0, bitRateBps));

    /// <summary>
    /// Raw (pre-FEC) bit error rate from Eb/N0 for a representative modulation family.
    /// PUBLIC STANDARD textbook curves; CALIBRATED_APPROXIMATION in that the specific modulation
    /// used by a given real legacy MIL-STD-188-181/183 waveform variant is not public at
    /// circuit-implementation fidelity (see SatcomNetDefinition.Modulation / SatcomModulation).
    /// </summary>
    public static double ModulationBer(double ebN0Db, SatcomModulation modulation)
    {
        var ebN0Linear = Math.Pow(10.0, ebN0Db / 10.0);
        var ber = modulation switch
        {
            // Noncoherent binary FSK: Pb = 0.5 * exp(-EbN0/2). Representative of legacy UHF
            // tactical/DAMA-era UHF SATCOM equipment (noncoherent detection is far more robust to
            // the Doppler/phase instability of a mobile airborne terminal than coherent schemes).
            SatcomModulation.NoncoherentFsk => 0.5 * Math.Exp(-ebN0Linear / 2.0),

            // Differential BPSK: Pb = 0.5 * exp(-EbN0).
            SatcomModulation.Dpsk => 0.5 * Math.Exp(-ebN0Linear),

            // Coherent BPSK: Pb = Q(sqrt(2*EbN0)) = 0.5*erfc(sqrt(EbN0)).
            SatcomModulation.Bpsk => 0.5 * Erfc(Math.Sqrt(ebN0Linear)),

            _ => 0.5 * Math.Exp(-ebN0Linear / 2.0)
        };
        return Math.Clamp(ber, 1e-12, 0.5);
    }

    /// <summary>Post-FEC BER: models FEC as an effective coding-gain shift applied to Eb/N0 before
    /// the modulation BER curve, rather than a specific MIL-STD-188-181 coding/interleaving scheme
    /// bit-for-bit -- i.e. FEC lets the link tolerate a weaker raw signal and still decode cleanly,
    /// up to its coding gain, then saturates like any real code. CALIBRATED_APPROXIMATION.</summary>
    public static double PostFecBer(double ebN0Db, double fecCodingGainDb, double fecStrength,
        SatcomModulation modulation)
    {
        var effectiveEbN0Db = ebN0Db + fecCodingGainDb * Math.Clamp(fecStrength, 0.0, 1.0);
        return ModulationBer(effectiveEbN0Db, modulation);
    }

    /// <summary>Frame error rate from a per-bit post-FEC BER and frame size, assuming independent
    /// bit errors (FER = 1-(1-BER)^bits) -- the baseline the channel-error model then correlates
    /// in time (burst behavior) rather than treating each frame as an independent coin flip.
    /// PHYSICAL_CALCULATION given the independent-errors assumption.</summary>
    public static double FrameErrorRate(double postFecBer, int frameBits)
    {
        if (postFecBer <= 0.0) return 0.0;
        if (postFecBer >= 0.5 || frameBits <= 0) return 1.0;
        return 1.0 - Math.Pow(1.0 - postFecBer, frameBits);
    }

    /// <summary>One-way propagation delay for a single leg, seconds: distance/speed-of-light.
    /// PHYSICAL_CALCULATION.</summary>
    public static double PropagationDelaySeconds(double slantRangeMeters) => slantRangeMeters / SpeedOfLightMps;

    /// <summary>Complementary error function via the Abramowitz &amp; Stegun 7.1.26 rational
    /// approximation (max error ~1.5e-7) -- .NET has no built-in erfc. PHYSICAL_CALCULATION
    /// (standard numerical approximation of an exact function).</summary>
    private static double Erfc(double x)
    {
        var sign = x < 0 ? -1.0 : 1.0;
        x = Math.Abs(x);

        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;

        var t = 1.0 / (1.0 + p * x);
        var y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
        return sign > 0 ? 1.0 - y : 1.0 + y;
    }
}
