using System;

namespace OpenFreq.Common.Satcom;

/// <summary>
/// CALIBRATED_APPROXIMATION -- not derived from any real A-10/ARC-210 antenna pattern (which isn't
/// publicly documented at this fidelity; see NAVAIR's ARC-210 page for the public-level
/// description of BLOS/SATCOM capability referenced in docs/SATCOM_SIMULATION.md). Models a
/// generic fuselage-spine-mounted UHF SATCOM blade antenna: best gain near boresight (straight up
/// from the airframe), rolling off toward the horizon, with the airframe itself (fuselage/wings)
/// shadowing the antenna once a satellite would be "below" it from the antenna's tilted point of
/// view.
///
/// Antenna orientation is derived from the airframe's own attitude (heading/pitch/bank) rather
/// than a full 3-axis antenna-frame rotation -- specifically, only the *magnitude* of how far the
/// airframe's local "up" has tilted from true vertical is used (the standard
/// acos(cos(bank)*cos(pitch)) identity), applied as a worst-case reduction to effective elevation
/// regardless of which way the tilt actually points relative to the satellite's azimuth. This
/// means a bank AWAY from the satellite is modeled the same as a bank INTO it -- a deliberate
/// simplification, not a claim of directional accuracy. See docs/SATCOM_SIMULATION.md.
/// </summary>
public static class SatcomAntennaModel
{
    /// <summary>Combined footprint (satellite antenna pattern) + airframe (terminal antenna
    /// pattern) gain, in dB. 0 dB at best, large negative when blocked/out of footprint.</summary>
    public static double CombinedGainDb(SatelliteLookAngles look, double geocentricFootprintAngleDeg,
        double footprintHalfPowerDeg, double footprintCutoffDeg,
        double? headingRad, double? pitchRad, double? bankRad)
    {
        var footprintGainDb = FootprintGainDb(geocentricFootprintAngleDeg, footprintHalfPowerDeg, footprintCutoffDeg);
        var terminalGainDb = TerminalAntennaGainDb(look.ElevationDeg, pitchRad, bankRad);
        return footprintGainDb + terminalGainDb;
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
    /// elevation and airframe tilt. See class doc for the tilt-magnitude simplification.</summary>
    public static double TerminalAntennaGainDb(double geometricElevationDeg, double? pitchRad, double? bankRad)
    {
        var tiltDeg = 0.0;
        if (pitchRad.HasValue && bankRad.HasValue)
        {
            var cosTilt = Math.Cos(bankRad.Value) * Math.Cos(pitchRad.Value);
            tiltDeg = Math.Acos(Math.Clamp(cosTilt, -1.0, 1.0)) * (180.0 / Math.PI);
        }

        var effectiveElevationDeg = geometricElevationDeg - tiltDeg;
        var offBoresightDeg = 90.0 - effectiveElevationDeg; // 0 = boresight (straight up), 90 = antenna's local horizon

        return offBoresightDeg switch
        {
            <= 30.0 => 0.0,                                              // boresight cone: full gain
            <= 70.0 => Lerp(offBoresightDeg, 30.0, 70.0, 0.0, -6.0),      // gradual rolloff
            <= 100.0 => Lerp(offBoresightDeg, 70.0, 100.0, -6.0, -20.0),  // approaching antenna's local horizon
            <= 130.0 => Lerp(offBoresightDeg, 100.0, 130.0, -20.0, -60.0), // airframe shadow onset
            _ => -100.0                                                   // fully blocked by the airframe
        };
    }

    private static double Lerp(double x, double x0, double x1, double y0, double y1) =>
        y0 + (y1 - y0) * Math.Clamp((x - x0) / (x1 - x0), 0.0, 1.0);
}
