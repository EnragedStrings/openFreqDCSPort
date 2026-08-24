namespace OpenFreq.Common.Satcom;

/// <summary>
/// Which physical SATCOM antenna is currently connected to the radio, for airframes with real
/// upper/lower antenna diversity (PROJECT_OBSERVED: the A-10C II's ARC-210 antenna-select switch,
/// cockpit argument 707 -- see docs/SATCOM_SIMULATION.md). Aircraft without diversity (everything
/// else today) are pinned to <see cref="Upper"/> and never read a switch at all.
/// </summary>
public enum SatcomAntennaSelection
{
    /// <summary>Spine/top-mounted antenna: best gain near zenith, shadowed by the airframe toward
    /// the horizon. Also the default/fallback for single-antenna airframes.</summary>
    Upper,

    /// <summary>Belly-mounted antenna: best gain near the horizon, shadowed by the airframe toward
    /// zenith. Default state for the A-10's switch before a clean first read -- see
    /// SatcomAntennaSelectorStateMachine.</summary>
    Lower
}
