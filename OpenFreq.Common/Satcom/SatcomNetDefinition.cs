using System;

namespace OpenFreq.Common.Satcom;

/// <summary>
/// Admin-configured SATCOM net/service: which satellite (or selection policy), which waveform,
/// separate uplink/downlink frequencies, and the RF/DSP parameters the link engine needs. Replaces
/// the earlier single-frequency SatcomProfile -- a real UHF SATCOM net is not simplex.
/// </summary>
public sealed class SatcomNetDefinition
{
    public required string NetId { get; init; }
    public required string DisplayName { get; init; }

    public SatelliteSelectionMode SelectionMode { get; init; } = SatelliteSelectionMode.Assigned;

    /// <summary>Required for Assigned/Manual; ignored for AutoBestVisible.</summary>
    public string? AssignedSatelliteId { get; init; }

    public SatcomWaveform Waveform { get; init; } = SatcomWaveform.Dama5k;

    /// <summary>Terminal-to-satellite carrier frequency, Hz. Real UHF MILSATCOM uplinks/downlinks
    /// use different sub-bands (e.g. terminal-to-satellite ~292-317 MHz, satellite-to-terminal
    /// ~243-270 MHz) -- PUBLIC-STANDARD band ranges; exact per-channel plans are config, not
    /// hardcoded here.</summary>
    public double UplinkHz { get; init; } = 300_000_000.0;

    public double DownlinkHz { get; init; } = 260_000_000.0;

    /// <summary>Channel bandwidth, Hz -- 5000 or 25000 for legacy UHF SATCOM. SOURCE_EXACT (the
    /// two legacy UHF SATCOM channel spacings).</summary>
    public double BandwidthHz { get; init; } = 5_000.0;

    public double MinElevationMaskDeg { get; init; } = 3.0;

    /// <summary>AutoBestVisible only: minimum combined-C/N0 improvement (dB) a candidate satellite
    /// must offer over the current one before a handover is even considered. GAMEPLAY_CONFIG.</summary>
    public double AutoSelectMarginHysteresisDb { get; init; } = 2.0;

    /// <summary>AutoBestVisible only: minimum seconds on the current satellite before another
    /// handover can occur. GAMEPLAY_CONFIG.</summary>
    public double AutoSelectMinHoldSeconds { get; init; } = 20.0;

    public double TxPowerWatts { get; init; } = 20.0;

    /// <summary>Receiver system noise temperature, Kelvin -- CALIBRATED_APPROXIMATION (real
    /// ARC-210 receiver G/T isn't publicly documented); ~500-800K is representative of a modest
    /// UHF tactical receiver front end.</summary>
    public double SystemNoiseTemperatureK { get; init; } = 600.0;

    /// <summary>Lumped stand-in for satellite antenna gain + transponder EIRP advantage, applied
    /// to both legs, scaled by the satellite's Health. CALIBRATED_APPROXIMATION -- the single
    /// biggest approximation in the link budget (real satellite EIRP/G-over-T figures aren't
    /// modeled individually). See docs/SATCOM_SIMULATION.md.</summary>
    public double SatelliteEirpAdjustDb { get; init; } = 25.0;

    public double ImplementationLossDb { get; init; } = 2.0;

    public SatcomModulation Modulation { get; init; } = SatcomModulation.NoncoherentFsk;

    /// <summary>Effective FEC coding gain, dB -- shifts Eb/N0 up before the BER curve.</summary>
    public double FecCodingGainDb { get; init; } = 6.0;

    public double FecStrength { get; init; } = 1.0;

    public int BitRateBps { get; init; } = 2400;

    public double FrameDurationSeconds { get; init; } = 0.0225;

    /// <summary>Eb/N0 (dB) required to move a link toward Good/acquire in the first place -- the
    /// higher of the two hysteresis thresholds. GAMEPLAY_CONFIG (tuned for perceptual behavior;
    /// real acquisition thresholds are waveform-specific and not fully public).</summary>
    public double AcquisitionEbN0ThresholdDb { get; init; } = 5.0;

    /// <summary>Eb/N0 (dB) below which an already-locked link starts degrading -- the lower of
    /// the two hysteresis thresholds, so marginal links don't rapidly connect/disconnect.
    /// GAMEPLAY_CONFIG.</summary>
    public double TrackingEbN0ThresholdDb { get; init; } = 1.0;

    /// <summary>Seconds a link may sit below the tracking threshold (Holdover) before being
    /// declared Lost. GAMEPLAY_CONFIG.</summary>
    public double HoldoverSeconds { get; init; } = 4.0;

    public int FrameBits => (int)Math.Round(BitRateBps * FrameDurationSeconds);
}
