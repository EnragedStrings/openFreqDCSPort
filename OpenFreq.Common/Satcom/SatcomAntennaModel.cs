using System;

namespace OpenFreq.Common.Satcom;

/// <summary>
/// CALIBRATED_APPROXIMATION -- not derived from any real ARC-210/airframe antenna pattern (which
/// isn't publicly documented at this fidelity; see NAVAIR's ARC-210 page for the public-level
/// description of BLOS/SATCOM capability referenced in docs/SATCOM_SIMULATION.md). Models two
/// generic fuselage-mounted UHF SATCOM blade antennas -- an upper (spine) antenna with best gain
/// near zenith, and a lower (belly) antenna with best gain near the local horizon -- each shadowed
/// by the airframe once a satellite would be on the wrong side of it. Which one is actually
/// connected to the radio is <see cref="SatcomAntennaSelection"/>, driven for real diversity-capable
/// airframes (currently just the A-10C II, cockpit argument 707) by
/// OpenFreq.Client's SatcomAntennaSelectorStateMachine; single-antenna airframes are pinned to
/// <see cref="SatcomAntennaSelection.Upper"/> and never read a switch.
///
/// PROJECT_OBSERVED (user-reported): the real switch's crossover convention is satellite look angle
/// at/below 30 degrees -> lower antenna, above 30 degrees -> upper antenna. The two curves below are
/// calibrated so their own "full gain" zones meet exactly at that boundary (upper: elevation >= 30,
/// lower: |elevation| &lt;= 30) -- flying with the switch in the wrong position for the actual
/// satellite elevation now genuinely costs you gain, which the previous single-antenna model could
/// never represent.
///
/// Antenna orientation is derived from the airframe's own attitude (heading/pitch/bank) rather
/// than a full 3-axis antenna-frame rotation -- specifically, only the *magnitude* of how far the
/// airframe's local "up" has tilted from true vertical is used (the standard
/// acos(cos(bank)*cos(pitch)) identity), applied as a worst-case ADDITIVE penalty directly on the
/// off-boresight angle (not a pre-shift of elevation -- see TerminalAntennaGainDb's own comment for
/// why those aren't equivalent once the boresight isn't at zenith), regardless of which way the
/// tilt actually points relative to the satellite's azimuth. This means a bank AWAY from the
/// satellite is modeled the same as a bank INTO it -- a deliberate simplification, not a claim of
/// directional accuracy -- and, for either antenna, more tilt can never improve gain for a fixed
/// satellite. See docs/SATCOM_SIMULATION.md.
/// </summary>
public static class SatcomAntennaModel
{
    /// <summary>Breakdown of one antenna-gain evaluation -- DEBUG-ONLY surface (see
    /// SatcomLegResult's own fields for which of these actually reach the wire under
    /// DebugAuthorized). Kept as a return value rather than only a total so
    /// SatcomServerCoordinator can expose the raw terms for live troubleshooting; candidate for
    /// trimming back down to just CombinedGainDb once the model is trusted -- see
    /// docs/SATCOM_SIMULATION.md.</summary>
    public readonly record struct GainDetail(
        double FootprintGainDb, double TerminalGainDb, double TiltDeg, double OffBoresightDeg, double CombinedGainDb);

    /// <summary>Combined footprint (satellite antenna pattern) + airframe (terminal antenna
    /// pattern) gain, in dB. 0 dB at best, large negative when blocked/out of footprint.</summary>
    public static GainDetail Evaluate(SatelliteLookAngles look, double geocentricFootprintAngleDeg,
        double footprintHalfPowerDeg, double footprintCutoffDeg,
        double? headingRad, double? pitchRad, double? bankRad, SatcomAntennaSelection antennaSelection)
    {
        var footprintGainDb = FootprintGainDb(geocentricFootprintAngleDeg, footprintHalfPowerDeg, footprintCutoffDeg);
        var (terminalGainDb, tiltDeg, offBoresightDeg) =
            TerminalAntennaGainDb(look.ElevationDeg, pitchRad, bankRad, antennaSelection);
        return new GainDetail(footprintGainDb, terminalGainDb, tiltDeg, offBoresightDeg,
            footprintGainDb + terminalGainDb);
    }

    /// <summary>Satellite-side footprint gain: 0 dB inside the half-power radius, smoothly
    /// rolling off to a hard cutoff.</summary>
    public static double FootprintGainDb(double geocentricAngleDeg, double footprintHalfPowerDeg,
        double footprintCutoffDeg)
    {
        if (geocentricAngleDeg <= footprintHalfPowerDeg)
            return 0.0;
        if (geocentricAngleDeg >= footprintCutoffDeg)
            return -100.0; // effectively unusable -- outside the transponder's coverage

        // Smoothstep from 0 dB at the half-power radius down to -100 dB at cutoff, passing
        // through -3 dB exactly at the half-power radius boundary for continuity, then
        // steepening -- a raised-cosine shape reads as "gradual then hard" rather than linear.
        var t = (geocentricAngleDeg - footprintHalfPowerDeg) / (footprintCutoffDeg - footprintHalfPowerDeg);
        var shaped = 1.0 - Math.Cos(t * Math.PI / 2.0); // 0..1, ease-in
        return -3.0 - shaped * 97.0;
    }

    /// <summary>Terminal-side (airframe-mounted antenna) gain as a function of true satellite
    /// elevation, airframe tilt, and which physical antenna is connected. See class doc for the
    /// tilt-magnitude simplification and the upper/lower crossover calibration.</summary>
    public static (double GainDb, double TiltDeg, double OffBoresightDeg) TerminalAntennaGainDb(
        double geometricElevationDeg, double? pitchRad, double? bankRad, SatcomAntennaSelection antennaSelection)
    {
        var tiltDeg = 0.0;
        if (pitchRad.HasValue && bankRad.HasValue)
        {
            var cosTilt = Math.Cos(bankRad.Value) * Math.Cos(pitchRad.Value);
            tiltDeg = Math.Acos(Math.Clamp(cosTilt, -1.0, 1.0)) * (180.0 / Math.PI);
        }

        // Tilt is applied as a pure ADDITIVE penalty on off-boresight angle, not a pre-shift of
        // elevation before computing off-boresight -- those are NOT equivalent once the boresight
        // isn't at elevation 90 (zenith). Pre-shifting elevation for the Lower antenna (boresight
        // at elevation 0) let tilt shift effective elevation TOWARD zero and accidentally IMPROVE
        // gain (caught by SatcomAntennaModelTests.MoreTiltNeverImprovesGainForAFixedSatellite) --
        // exactly the shape of bug this model exists to avoid. Adding the penalty on top of
        // off-boresight instead guarantees tilt can only ever move AWAY from boresight, for either
        // antenna, regardless of which direction "toward boresight" happens to be.
        var baseOffBoresightDeg = antennaSelection == SatcomAntennaSelection.Upper
            ? 90.0 - geometricElevationDeg   // boresight = zenith: 0 = boresight, 90 = local horizon
            : Math.Abs(geometricElevationDeg); // boresight = local horizon (elevation 0), not nadir --
                                                // see class doc for why a belly antenna's usable
                                                // pattern is toward the horizon, not straight down.
        var offBoresightDeg = baseOffBoresightDeg + tiltDeg;

        var gainDb = antennaSelection == SatcomAntennaSelection.Upper
            ? offBoresightDeg switch
            {
                // Full-gain zone extends down to 30deg elevation (offBoresight 60) to meet the
                // lower antenna's own full-gain ceiling at the same boundary -- see class doc.
                <= 60.0 => 0.0,                                               // boresight cone: full gain
                <= 100.0 => Lerp(offBoresightDeg, 60.0, 100.0, 0.0, -6.0),     // gradual rolloff
                <= 130.0 => Lerp(offBoresightDeg, 100.0, 130.0, -6.0, -20.0),  // approaching local horizon
                <= 160.0 => Lerp(offBoresightDeg, 130.0, 160.0, -20.0, -60.0), // airframe shadow onset
                _ => -100.0                                                    // fully blocked by the airframe
            }
            : offBoresightDeg switch
            {
                <= 30.0 => 0.0,                                               // boresight cone: full gain
                <= 70.0 => Lerp(offBoresightDeg, 30.0, 70.0, 0.0, -6.0),       // gradual rolloff
                <= 100.0 => Lerp(offBoresightDeg, 70.0, 100.0, -6.0, -20.0),   // approaching zenith
                <= 130.0 => Lerp(offBoresightDeg, 100.0, 130.0, -20.0, -60.0), // airframe shadow onset
                _ => -100.0                                                    // fully blocked by the airframe
            };

        return (gainDb, tiltDeg, offBoresightDeg);
    }

    private static double Lerp(double x, double x0, double x1, double y0, double y1) =>
        y0 + (y1 - y0) * Math.Clamp((x - x0) / (x1 - x0), 0.0, 1.0);
}
