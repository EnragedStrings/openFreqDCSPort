using OpenFreq.Common.Satcom;

namespace OpenFreq.Common.Tests.Satcom;

/// <summary>
/// Covers SatcomAntennaModel's upper/lower antenna curves and the tilt-penalty math directly --
/// this pass split the previous single-antenna model into two (see SatcomAntennaSelection), and
/// this file existing at all is new; there was no direct antenna-model coverage before (only
/// indirectly via SatcomLinkEngineCoreTests). Also see docs/SATCOM_SIMULATION.md.
/// </summary>
public class SatcomAntennaModelTests
{
    [Theory]
    [InlineData(90.0)] // zenith
    [InlineData(60.0)]
    [InlineData(30.0)] // the stated crossover -- upper's full-gain floor
    public void UpperAntennaHasFullGainAtOrAboveThirtyDegreesElevationWingsLevel(double elevationDeg)
    {
        var (gainDb, tiltDeg, _) = SatcomAntennaModel.TerminalAntennaGainDb(
            elevationDeg, pitchRad: 0.0, bankRad: 0.0, SatcomAntennaSelection.Upper);

        Assert.Equal(0.0, tiltDeg);
        Assert.Equal(0.0, gainDb);
    }

    [Fact]
    public void UpperAntennaDegradesBelowThirtyDegreesElevation()
    {
        var (gainDb, _, _) = SatcomAntennaModel.TerminalAntennaGainDb(
            10.0, pitchRad: 0.0, bankRad: 0.0, SatcomAntennaSelection.Upper);

        Assert.True(gainDb < 0.0);
    }

    [Theory]
    [InlineData(0.0)] // horizon
    [InlineData(-15.0)]
    [InlineData(30.0)] // the stated crossover -- lower's full-gain ceiling
    [InlineData(-30.0)]
    public void LowerAntennaHasFullGainWithinThirtyDegreesOfHorizonWingsLevel(double elevationDeg)
    {
        var (gainDb, tiltDeg, _) = SatcomAntennaModel.TerminalAntennaGainDb(
            elevationDeg, pitchRad: 0.0, bankRad: 0.0, SatcomAntennaSelection.Lower);

        Assert.Equal(0.0, tiltDeg);
        Assert.Equal(0.0, gainDb);
    }

    [Fact]
    public void LowerAntennaDegradesNearZenith()
    {
        var (gainDb, _, _) = SatcomAntennaModel.TerminalAntennaGainDb(
            80.0, pitchRad: 0.0, bankRad: 0.0, SatcomAntennaSelection.Lower);

        Assert.True(gainDb < 0.0);
    }

    [Fact]
    public void UpperAntennaGainAtThirtyDegreesBeatsLowerAntennaGainAtSameElevation()
    {
        // The whole point of the switch: at exactly the boundary the aircraft should be on Upper,
        // and Upper should be at least as good as Lower there (both are 0 dB at this exact
        // boundary by calibration, but this pins the relationship so a future recalibration can't
        // silently invert it).
        var upper = SatcomAntennaModel.TerminalAntennaGainDb(30.0, 0.0, 0.0, SatcomAntennaSelection.Upper);
        var lower = SatcomAntennaModel.TerminalAntennaGainDb(30.0, 0.0, 0.0, SatcomAntennaSelection.Lower);

        Assert.True(upper.GainDb >= lower.GainDb);
    }

    [Fact]
    public void WrongAntennaForLowElevationIsWorseThanRightAntenna()
    {
        // A low-elevation satellite (10deg) with the switch on Upper (spine antenna, shadowed
        // toward the horizon) should be worse than the same geometry with Lower selected -- this
        // is the whole reason the switch matters, and the previous single-antenna model could never
        // represent a "wrong switch position" penalty at all.
        var wrongAntenna = SatcomAntennaModel.TerminalAntennaGainDb(10.0, 0.0, 0.0, SatcomAntennaSelection.Upper);
        var rightAntenna = SatcomAntennaModel.TerminalAntennaGainDb(10.0, 0.0, 0.0, SatcomAntennaSelection.Lower);

        Assert.True(wrongAntenna.GainDb < rightAntenna.GainDb);
    }

    [Theory]
    [InlineData(SatcomAntennaSelection.Upper)]
    [InlineData(SatcomAntennaSelection.Lower)]
    public void MoreTiltNeverImprovesGainForAFixedSatellite(SatcomAntennaSelection selection)
    {
        // Documents/guards the invariant the tilt-penalty formula is built on: tiltDeg is a
        // magnitude (acos), always subtracted from elevation, so increasing bank in EITHER
        // direction can only hold gain flat or make it worse for a fixed true satellite elevation.
        // If this ever fails, gain got directionally wrong somewhere -- exactly the shape of bug
        // reported as "SATCOM got BETTER at 90deg bank".
        const double elevationDeg = 45.0;
        var level = SatcomAntennaModel.TerminalAntennaGainDb(elevationDeg, 0.0, 0.0, selection);
        var banked45 = SatcomAntennaModel.TerminalAntennaGainDb(elevationDeg, 0.0, 45.0 * System.Math.PI / 180.0, selection);
        var banked90 = SatcomAntennaModel.TerminalAntennaGainDb(elevationDeg, 0.0, 90.0 * System.Math.PI / 180.0, selection);

        Assert.True(level.GainDb >= banked45.GainDb);
        Assert.True(banked45.GainDb >= banked90.GainDb);
    }

    [Fact]
    public void FootprintGainIsFullInsideHalfPowerRadius()
    {
        var gainDb = SatcomAntennaModel.FootprintGainDb(1.0, footprintHalfPowerDeg: 5.0, footprintCutoffDeg: 10.0);
        Assert.Equal(0.0, gainDb);
    }

    [Fact]
    public void FootprintGainIsFullyUnusableAtOrBeyondCutoff()
    {
        var gainDb = SatcomAntennaModel.FootprintGainDb(10.0, footprintHalfPowerDeg: 5.0, footprintCutoffDeg: 10.0);
        Assert.Equal(-100.0, gainDb);
    }
}
