namespace OpenFreq.Common.Satcom;

/// <summary>Geodetic state of one terminal (aircraft), as much as is known -- for the remote peer
/// this is frequently an approximation (see docs/SATCOM_SIMULATION.md's TX/RX distance
/// limitation) rather than their own real telemetry.</summary>
public readonly record struct SatcomTerminalState(
    double LatitudeDeg, double LongitudeDeg, double AltitudeMeters,
    double? HeadingRad, double? PitchRad, double? BankRad,
    bool AttitudeIsApproximate);

/// <summary>One evaluated leg (terminal-to-satellite uplink, or satellite-to-terminal downlink).</summary>
public readonly record struct SatcomLegResult(
    double ElevationDeg, double AzimuthDeg, double SlantRangeMeters, double AntennaGainDb,
    double PathLossDb, double Cn0DbHz, double PropagationSeconds,
    bool AboveElevationMask, bool EarthOccluded, string UnavailableReason)
{
    public bool Usable => AboveElevationMask && !EarthOccluded && AntennaGainDb > -100.0;
}

/// <summary>
/// Full SATCOM link-quality snapshot for one transmission to one recipient -- everything the
/// debug telemetry surfaces, and what the server-side channel-error model consumes to decide
/// frame dispositions. Computed authoritatively by the server (SatcomLinkEngine); the client only
/// ever displays a copy of this pushed down in a SatcomLinkStateMessage.
/// </summary>
public sealed record SatcomLinkResult
{
    public required string SatelliteId { get; init; }
    public required string SatelliteName { get; init; }

    public required SatcomLegResult Uplink { get; init; }
    public required SatcomLegResult Downlink { get; init; }

    /// <summary>Combined end-to-end C/N0, dB-Hz -- both legs combined in the LINEAR domain per
    /// SatcomLinkBudget.CombineCarrierToNoiseDensityDbHz, not an average/soft-min of dB margins.</summary>
    public required double CombinedCn0DbHz { get; init; }

    public required double EbN0Db { get; init; }
    public required double RawBer { get; init; }
    public required double PostFecBer { get; init; }
    public required double FrameErrorRate { get; init; }

    /// <summary>Total one-way propagation latency (terminal -&gt; satellite -&gt; terminal),
    /// seconds -- does NOT include vocoder/modem/DAMA-queue/jitter-buffer delay, which are added
    /// separately (see docs/SATCOM_SIMULATION.md's latency section).</summary>
    public required double PropagationLatencySeconds { get; init; }

    public required SatcomLinkQualityState QualityState { get; init; }

    /// <summary>False when either leg is unusable (below horizon/mask, Earth-occluded, or outside
    /// antenna/footprint pattern) or the satellite is disabled/unhealthy -- distinct from merely a
    /// poor margin.</summary>
    public required bool LinkAvailable { get; init; }

    public required SatcomAcquisitionFailureReason FailureReason { get; init; }
    public required string UnavailableReason { get; init; }

    public static SatcomLinkResult Unavailable(SatcomAcquisitionFailureReason reason, string message)
    {
        var emptyLeg = new SatcomLegResult(0, 0, 0, -100, 0, -999, 0, false, false, message);
        return new SatcomLinkResult
        {
            SatelliteId = "", SatelliteName = "",
            Uplink = emptyLeg, Downlink = emptyLeg,
            CombinedCn0DbHz = -999, EbN0Db = -999,
            RawBer = 0.5, PostFecBer = 0.5, FrameErrorRate = 1.0,
            PropagationLatencySeconds = 0.0,
            QualityState = SatcomLinkQualityState.Lost,
            LinkAvailable = false,
            FailureReason = reason,
            UnavailableReason = message
        };
    }
}
