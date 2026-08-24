using System;
using OpenFreq.Common.Satcom;

namespace OpenFreq.Client.Services.Satcom;

/// <summary>
/// Tracks the A-10C II ARC-210's upper/lower SATCOM antenna selector switch (PROJECT_OBSERVED,
/// user-reported: cockpit argument 707 -- 1.0 = upper antenna, 0.0 = lower antenna). Real 3-position
/// switch: the 0.5 middle detent is not a third selectable antenna, it's the switch mid-travel (or
/// simply not read cleanly yet) -- it must leave whichever antenna was last actively selected
/// intact, not be interpreted as "neither"/"blocked".
///
/// Deliberately a plain latch, not a debounce/timer state machine like
/// <see cref="SatcomAcquisitionStateMachine"/> -- there's no acquisition delay to model here, only
/// "which of two known-good values was most recently seen".
///
/// Defaults to <see cref="SatcomAntennaSelection.Lower"/> before the first clean read (explicit
/// product decision, not a physical fact -- DCS doesn't report an initial switch position any more
/// than it reports the ARC-210 does).
/// </summary>
public sealed class SatcomAntennaSelectorStateMachine
{
    /// <summary>How close the raw argument value must be to 1.0/0.0 to count as a clean upper/lower
    /// read. Matches the tolerance convention already used for the SATCOM channel-knob/selector
    /// detection in OpenFreqDCSConfig.lua's a10c2.satcom block.</summary>
    public const double Tolerance = 0.05;

    public SatcomAntennaSelection Selection { get; private set; } = SatcomAntennaSelection.Lower;

    /// <summary>Advance with the latest raw DCS cockpit-argument value (null when unavailable,
    /// e.g. not in the A-10 or the export hasn't populated it yet -- also leaves the last selection
    /// intact).</summary>
    public SatcomAntennaSelection Update(double? rawValue)
    {
        if (rawValue is { } value)
        {
            if (Math.Abs(value - 1.0) <= Tolerance)
                Selection = SatcomAntennaSelection.Upper;
            else if (Math.Abs(value - 0.0) <= Tolerance)
                Selection = SatcomAntennaSelection.Lower;
            // else: mid-travel (including the 0.5 detent) -- leave Selection as it was.
        }

        return Selection;
    }
}
