using System;

namespace OpenFreq.Common.Satcom;

/// <summary>
/// Drives <see cref="SatcomLinkQualityState"/> from a per-tick Eb/N0 sample using SEPARATE
/// acquisition and tracking thresholds with hysteresis, so a marginal link doesn't rapidly
/// connect/disconnect: a higher Eb/N0 bar is required to reach Good from below, a lower one is
/// tolerated to stay locked once there, and a Holdover grace period sits between Degraded and Lost
/// so one bad sample doesn't instantly drop the call. Timestamp-based like
/// SatcomAcquisitionStateMachine/SatcomDamaStateMachine elsewhere in this codebase -- no
/// sleep/blocking, deterministic, unit-testable with an injected "now". One instance per
/// (client, net) pair, owned by the server's link engine.
/// </summary>
public sealed class SatcomLinkQualityTracker
{
    /// <summary>Width of the Degraded band below the tracking threshold, dB, before a link is
    /// considered "below tracking" and Holdover starts. GAMEPLAY_CONFIG.</summary>
    public const double DegradedBandDb = 3.0;

    public SatcomLinkQualityState State { get; private set; } = SatcomLinkQualityState.Lost;

    private long? _holdoverStartedAtMs;

    /// <summary>Advances the tracker. <paramref name="ebN0Db"/> should be -999 (or any value well
    /// below the tracking threshold) when the link is entirely unavailable this tick (e.g. below
    /// horizon, Earth-occluded, no satellite assigned).</summary>
    public SatcomLinkQualityState Update(double ebN0Db, SatcomNetDefinition net, long nowMs)
    {
        var meetsAcquisition = ebN0Db >= net.AcquisitionEbN0ThresholdDb;
        var meetsTracking = ebN0Db >= net.TrackingEbN0ThresholdDb;
        var meetsDegradedFloor = ebN0Db >= net.TrackingEbN0ThresholdDb - DegradedBandDb;

        switch (State)
        {
            case SatcomLinkQualityState.Lost:
                if (meetsAcquisition)
                    TransitionTo(SatcomLinkQualityState.Good, nowMs);
                break;

            case SatcomLinkQualityState.Good:
                if (!meetsTracking)
                    TransitionTo(meetsDegradedFloor ? SatcomLinkQualityState.Degraded : SatcomLinkQualityState.Holdover, nowMs);
                else if (!meetsAcquisition)
                    TransitionTo(SatcomLinkQualityState.Marginal, nowMs);
                break;

            case SatcomLinkQualityState.Marginal:
                if (meetsAcquisition)
                    TransitionTo(SatcomLinkQualityState.Good, nowMs);
                else if (!meetsTracking)
                    TransitionTo(meetsDegradedFloor ? SatcomLinkQualityState.Degraded : SatcomLinkQualityState.Holdover, nowMs);
                break;

            case SatcomLinkQualityState.Degraded:
                if (meetsTracking)
                    TransitionTo(meetsAcquisition ? SatcomLinkQualityState.Good : SatcomLinkQualityState.Marginal, nowMs);
                else if (!meetsDegradedFloor)
                    TransitionTo(SatcomLinkQualityState.Holdover, nowMs);
                break;

            case SatcomLinkQualityState.Holdover:
                if (meetsTracking)
                {
                    TransitionTo(meetsAcquisition ? SatcomLinkQualityState.Good : SatcomLinkQualityState.Marginal, nowMs);
                }
                else if (ElapsedHoldoverSeconds(nowMs) >= net.HoldoverSeconds)
                {
                    TransitionTo(SatcomLinkQualityState.Lost, nowMs);
                }
                break;
        }

        return State;
    }

    private double ElapsedHoldoverSeconds(long nowMs) =>
        Math.Max(0.0, (nowMs - (_holdoverStartedAtMs ?? nowMs)) / 1000.0);

    private void TransitionTo(SatcomLinkQualityState next, long nowMs)
    {
        if (next == State) return;
        if (next == SatcomLinkQualityState.Holdover) _holdoverStartedAtMs = nowMs;
        State = next;
    }

    public void Reset()
    {
        State = SatcomLinkQualityState.Lost;
        _holdoverStartedAtMs = null;
    }
}
