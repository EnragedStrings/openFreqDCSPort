using System;
using System.Collections.Generic;
using System.Linq;
using OpenFreq.Common.Jamming;
using OpenFreqAudio.Jamming;

namespace OpenFreq.Common.Satcom;

/// <summary>One jammer as the SATCOM link engine sees it: a located, radiating jammer and what it radiates on.</summary>
/// <param name="Resolved">The resolved definition (power, antenna, technique).</param>
/// <param name="AltitudeMeters">Antenna altitude MSL, m.</param>
/// <param name="Emissions">What it radiates on right now (already empty when it is off, dead or out of schedule).</param>
public sealed record SatcomJammerSource(
    JammerDefinition Resolved,
    double LatitudeDeg,
    double LongitudeDeg,
    double AltitudeMeters,
    IReadOnlyList<JammerEmission> Emissions)
{
    public string Id => Resolved.Id;
    public string DisplayName => string.IsNullOrWhiteSpace(Resolved.Name) ? Resolved.Id : Resolved.Name!;
}

/// <summary>What the jammers did to one SATCOM link (docs/jamming/JAMMING_DESIGN.md phase 4).</summary>
public sealed record SatcomJammingEffect
{
    public static readonly SatcomJammingEffect None = new();

    /// <summary>Total jammer power arriving in the uplink channel at the satellite, dBW (null: none).</summary>
    public double? UplinkJammerDbw { get; init; }

    /// <summary>Uplink J/S at the satellite input, dB (null: no uplink jammer).</summary>
    public double? UplinkJsDb { get; init; }

    /// <summary>Downlink power the wanted signal lost to the jammer at the hard-limiting transponder (power robbing
    /// plus small-signal suppression), dB ≥ 0.</summary>
    public double DownlinkRobbingLossDb { get; init; }

    /// <summary>Local downlink jammer-to-noise ratio at the receiver, dB (null: none reaches it).</summary>
    public double? DownlinkJnrDb { get; init; }

    /// <summary>Drop in end-to-end C/N0 the jammers caused, dB ≥ 0.</summary>
    public double DegradationDb { get; init; }

    /// <summary>DAMA control/orderwire channel jammed at the satellite: no new assignments.</summary>
    public bool OrderwireJammed { get; init; }

    /// <summary>The jammer contributing the most, for the UI (null: none).</summary>
    public string? DominantJammerId { get; init; }
    public string? DominantJammerName { get; init; }

    public bool Any => DegradationDb > 0.05 || OrderwireJammed;
}

/// <summary>
/// UHF SATCOM jamming (research-02 §3, research-03 Tirada). Legacy UHF SATCOM is a non-regenerative, hard-limited
/// bent pipe with no processing gain, so:
/// <list type="number">
/// <item>An <b>uplink</b> jammer anywhere in the satellite's footprint adds I0 = J/B to the uplink N0 of every user
/// on that channel, and the transponder relays it to all of them.</item>
/// <item>The hard limiter shares its fixed output power in proportion to input power: the wanted signal gets
/// S/(S+J+N) of it instead of S/(S+N) (<b>power robbing</b>), and a strong interferer suppresses a weak signal by a
/// further ≈1 dB (noise-like) to 6 dB (carrier) (Jones 1963; Davenport 1953).</item>
/// <item>A <b>downlink</b> jammer only hurts receivers it reaches directly (the satellite's downlink is weak, so it
/// works at short range). An aircraft's top-mounted SATCOM antenna is shielded from a jammer below its horizon
/// plane.</item>
/// <item><b>DAMA</b>: jamming the control (orderwire) channel at the satellite denies channel assignment to the
/// whole network.</item>
/// </list>
/// Pure functions: the server's SatcomLinkEngine applies them to its leg results.
/// </summary>
public static class SatcomJamming
{
    /// <summary>Most a strong carrier (CW-like) interferer suppresses a weak signal in a hard limiter, dB
    /// (Jones 1963: 6 dB). PUB theory.</summary>
    public const double MaxCarrierSuppressionDb = 6.0;

    /// <summary>Most strong Gaussian-noise interference suppresses a weak signal in a hard limiter, dB
    /// (Davenport 1953: π/4 ≈ −1.05 dB). PUB theory.</summary>
    public const double MaxNoiseSuppressionDb = 1.05;

    /// <summary>Extra loss toward a downlink jammer below an aircraft's horizon plane (top-mounted SATCOM antenna,
    /// fuselage shielding), dB. EST (research-02 §6.6: 5–15, default 10).</summary>
    public const double AircraftShieldingDb = 10.0;

    /// <summary>An aircraft is "airborne" for the shielding rule above this height over the jammer, m.</summary>
    public const double AirborneHeightMeters = 100.0;

    /// <summary>Lowest elevation at which a jammer's antenna can reach the satellite, degrees.</summary>
    public const double JammerMinElevationDeg = 0.0;

    /// <summary>Default EIRP of the DAMA control station's orderwire, dBW. EST (a ground control station: ~200 W into
    /// a 7 dBi antenna).</summary>
    public const double DefaultOrderwireEirpDbw = 30.0;

    /// <summary>Default J/S at the satellite at which the orderwire is lost, dB. EST (no processing gain; the
    /// legacy orderwire is a narrowband FSK/PSK burst like the traffic).</summary>
    public const double DefaultOrderwireKillJsDb = 0.0;

    /// <summary>Jammer power falling in a channel, as a share (0..) of one PA's TxPowerW: Σ PowerFraction × in-band
    /// fraction. 0 when nothing overlaps.</summary>
    public static double InChannelPowerShare(IEnumerable<JammerEmission> emissions, double channelCenterHz,
        double channelBandwidthHz)
    {
        var centerKhz = channelCenterHz / 1000.0;
        var bwKhz = Math.Max(0.001, channelBandwidthHz / 1000.0);
        return emissions.Sum(e =>
            Math.Max(0.0, e.PowerFraction) * JammingMath.InBandFraction(e.CenterKhz, e.BandwidthKhz, centerKhz, bwKhz));
    }

    /// <summary>The jammer's antenna gain toward the sky (its SATCOM gain if set, else its normal gain), dBi.</summary>
    public static double SkyGainDbi(JammerDefinition resolved) =>
        resolved.SatcomAntennaGainDbi ?? resolved.AntennaGainDbi ?? 0.0;

    /// <summary>
    /// Jammer power arriving in a satellite uplink channel, dBW, or null when it radiates nothing there, the
    /// satellite is below its horizon or Earth-occluded, or it is outside the satellite's footprint. Uses the same
    /// lumped satellite receive advantage (<see cref="SatcomNetDefinition.SatelliteEirpAdjustDb"/> × health) and
    /// footprint roll-off as a user's uplink, so J and S are on the same scale.
    /// </summary>
    public static double? UplinkJammerDbw(SatcomJammerSource jammer, SatcomNetDefinition net,
        SatcomSatelliteDefinition satellite, SatcomSatellitePosition satPos, double uplinkHz)
    {
        var share = InChannelPowerShare(jammer.Emissions, uplinkHz, net.BandwidthHz);
        if (share <= 0.0) return null;

        var satEcef = satPos.ToEcef();
        var look = SatcomGeodesy.LookAngles(jammer.LatitudeDeg, jammer.LongitudeDeg, jammer.AltitudeMeters, satEcef);
        if (look.ElevationDeg < JammerMinElevationDeg) return null;
        var jammerEcef = SatcomGeodesy.GeodeticToEcef(jammer.LatitudeDeg, jammer.LongitudeDeg, jammer.AltitudeMeters);
        if (SatcomGeodesy.EllipsoidOccludes(jammerEcef, satEcef)) return null;

        var footprintAngle = SatcomGeodesy.GeocentricAngleFromSubsatellite(satPos.LatitudeDeg, satPos.LongitudeDeg,
            jammer.LatitudeDeg, jammer.LongitudeDeg);
        var footprintDb = SatcomAntennaModel.FootprintGainDb(footprintAngle, satellite.FootprintHalfPowerDeg,
            satellite.FootprintCutoffDeg);
        if (footprintDb <= -100.0) return null;

        var r = jammer.Resolved;
        var eirpDbw = JammingMath.WattsToDbw((r.TxPowerW ?? 0.0) * share) + SkyGainDbi(r) - (r.FeederLossDb ?? 0.0) +
                      JammingMath.SectorPatternDb(r.Pattern ?? JammerAntennaPattern.Omni, r.AzimuthDeg ?? 0.0,
                          r.BeamwidthDeg ?? JammerPresets.DefaultBeamwidthDeg,
                          r.FrontToBackDb ?? JammerPresets.DefaultFrontToBackDb, look.AzimuthDeg);
        var pathLossDb = SatcomLinkBudget.FreeSpacePathLossDb(look.SlantRangeMeters, uplinkHz);
        return eirpDbw - pathLossDb + footprintDb + satellite.Health * net.SatelliteEirpAdjustDb;
    }

    /// <summary>
    /// Local jammer power arriving at a SATCOM receiver in its downlink channel, dBW, or null when it radiates
    /// nothing there or the receiver is beyond the radio horizon. Receive antenna 0 dBi, less
    /// <see cref="AircraftShieldingDb"/> for an airborne receiver above the jammer (the jammer is below its
    /// top-mounted antenna's horizon plane). Free space within the horizon; terrain is not modelled.
    /// </summary>
    public static double? DownlinkJammerDbw(SatcomJammerSource jammer, SatcomNetDefinition net,
        SatcomTerminalState receiver, double downlinkHz)
    {
        var share = InChannelPowerShare(jammer.Emissions, downlinkHz, net.BandwidthHz);
        if (share <= 0.0) return null;

        var jammerEcef = SatcomGeodesy.GeodeticToEcef(jammer.LatitudeDeg, jammer.LongitudeDeg, jammer.AltitudeMeters);
        var rxEcef = SatcomGeodesy.GeodeticToEcef(receiver.LatitudeDeg, receiver.LongitudeDeg,
            receiver.AltitudeMeters);
        var distanceM = Math.Max(1.0, (rxEcef - jammerEcef).Length());
        if (distanceM / 1000.0 > JammingMath.RadioHorizonKm(jammer.AltitudeMeters, receiver.AltitudeMeters))
            return null;

        var r = jammer.Resolved;
        var bearing = JammingMath.GeodeticBearingDeg(jammer.LatitudeDeg, jammer.LongitudeDeg, receiver.LatitudeDeg,
            receiver.LongitudeDeg);
        var eirpDbw = JammingMath.EirpDbm(r, bearing, share) - 30.0;
        var shieldingDb = receiver.AltitudeMeters - jammer.AltitudeMeters > AirborneHeightMeters
            ? AircraftShieldingDb
            : 0.0;
        return eirpDbw - SatcomLinkBudget.FreeSpacePathLossDb(distanceM, downlinkHz) - shieldingDb;
    }

    /// <summary>Weak-signal suppression in a hard limiter, dB: max × x/(1+x) with x = J/S (linear) — ≈0 for a weak
    /// jammer, → max for a strong one. Carrier-like techniques reach 6 dB, noise-like ≈1 dB.</summary>
    public static double LimiterSuppressionDb(double jsDb, JammerTechnique technique)
    {
        var max = technique is JammerTechnique.Tone or JammerTechnique.MultiTone or JammerTechnique.Swept
            or JammerTechnique.SteppedTones or JammerTechnique.Gulls
            ? MaxCarrierSuppressionDb
            : MaxNoiseSuppressionDb;
        var x = Math.Pow(10.0, jsDb / 10.0);
        return max * x / (1.0 + x);
    }

    /// <summary>
    /// Applies the jammers to an evaluated, usable link: returns the uplink and downlink legs with their C/N0
    /// replaced by C/(N0+I0) (and the downlink also reduced by power robbing), and the effect for the UI. Legs that
    /// are not usable, and an empty jammer list, come back unchanged with <see cref="SatcomJammingEffect.None"/>.
    /// </summary>
    public static (SatcomLegResult Uplink, SatcomLegResult Downlink, SatcomJammingEffect Effect) Apply(
        SatcomLegResult uplink, SatcomLegResult downlink, IReadOnlyList<SatcomJammerSource> jammers,
        SatcomNetDefinition net, SatcomSatelliteDefinition satellite, SatcomSatellitePosition satPos,
        SatcomTerminalState receiver, double uplinkHz, double downlinkHz)
    {
        if (jammers.Count == 0 || !uplink.Usable || !downlink.Usable)
            return (uplink, downlink, SatcomJammingEffect.None);

        var n0Dbw = SatcomLinkBudget.NoiseDensityDbwPerHz(net.SystemNoiseTemperatureK);
        var bandwidthDb = 10.0 * Math.Log10(Math.Max(1.0, net.BandwidthHz));
        var noiseInChannel = Math.Pow(10.0, (n0Dbw + bandwidthDb) / 10.0);

        // ---- uplink: J at the satellite input, summed over jammers ----
        double jUp = 0.0;
        string? dominantId = null, dominantName = null;
        double dominantW = 0.0;
        var dominantTechnique = JammerTechnique.Noise;
        foreach (var jammer in jammers)
        {
            if (UplinkJammerDbw(jammer, net, satellite, satPos, uplinkHz) is not { } dbw) continue;
            var w = Math.Pow(10.0, dbw / 10.0);
            jUp += w;
            if (w > dominantW)
            {
                dominantW = w;
                dominantId = jammer.Id;
                dominantName = jammer.DisplayName;
                dominantTechnique = jammer.Resolved.Technique ?? JammerTechnique.Noise;
            }
        }

        // Wanted signal power at the satellite input, from its C/N0.
        var sUpDbw = uplink.Cn0DbHz + n0Dbw;
        var sUp = Math.Pow(10.0, sUpDbw / 10.0);

        double? uplinkJammerDbw = null, uplinkJsDb = null;
        var robbingDb = 0.0;
        var newUplink = uplink;
        if (jUp > 0.0)
        {
            uplinkJammerDbw = 10.0 * Math.Log10(jUp);
            uplinkJsDb = uplinkJammerDbw - sUpDbw;
            var i0OverN0 = jUp / noiseInChannel; // I0/N0 = (J/B)/N0
            newUplink = uplink with { Cn0DbHz = uplink.Cn0DbHz - 10.0 * Math.Log10(1.0 + i0OverN0) };

            // Hard limiter: the wanted signal's share of the fixed output power falls from S/(S+N) to S/(S+J+N),
            // and a strong interferer suppresses it further.
            robbingDb = 10.0 * Math.Log10((sUp + jUp + noiseInChannel) / (sUp + noiseInChannel)) +
                        LimiterSuppressionDb(uplinkJsDb.Value, dominantTechnique);
        }

        // ---- downlink: robbing, then any local jammer at the receiver ----
        var newDownlink = downlink with { Cn0DbHz = downlink.Cn0DbHz - robbingDb };
        double jDown = 0.0, dominantDownW = 0.0;
        string? dominantDownId = null, dominantDownName = null;
        foreach (var jammer in jammers)
        {
            if (DownlinkJammerDbw(jammer, net, receiver, downlinkHz) is not { } dbw) continue;
            var w = Math.Pow(10.0, dbw / 10.0);
            jDown += w;
            if (w > dominantDownW)
            {
                dominantDownW = w;
                dominantDownId = jammer.Id;
                dominantDownName = jammer.DisplayName;
            }
        }

        double? downlinkJnrDb = null;
        if (jDown > 0.0)
        {
            var jnr = jDown / noiseInChannel;
            downlinkJnrDb = 10.0 * Math.Log10(jnr);
            newDownlink = newDownlink with { Cn0DbHz = newDownlink.Cn0DbHz - 10.0 * Math.Log10(1.0 + jnr) };
        }

        var before = SatcomLinkBudget.CombineCarrierToNoiseDensityDbHz(uplink.Cn0DbHz, downlink.Cn0DbHz);
        var after = SatcomLinkBudget.CombineCarrierToNoiseDensityDbHz(newUplink.Cn0DbHz, newDownlink.Cn0DbHz);
        var degradation = Math.Max(0.0, before - after);

        // The UI names whichever path did more harm: the uplink jammer's degradation alone vs the downlink's.
        if (dominantDownId != null)
        {
            var upOnly = SatcomLinkBudget.CombineCarrierToNoiseDensityDbHz(newUplink.Cn0DbHz,
                downlink.Cn0DbHz - robbingDb);
            if (dominantId == null || upOnly - after > before - upOnly)
            {
                dominantId = dominantDownId;
                dominantName = dominantDownName;
            }
        }

        var effect = new SatcomJammingEffect
        {
            UplinkJammerDbw = uplinkJammerDbw,
            UplinkJsDb = uplinkJsDb,
            DownlinkRobbingLossDb = robbingDb,
            DownlinkJnrDb = downlinkJnrDb,
            DegradationDb = degradation,
            DominantJammerId = degradation > 0.05 ? dominantId : null,
            DominantJammerName = degradation > 0.05 ? dominantName : null
        };
        return (newUplink, newDownlink, effect);
    }

    /// <summary>
    /// Whether a DAMA net's control (orderwire) channel is jammed at the satellite: total uplink jammer power on
    /// the orderwire frequency is at least <see cref="SatcomNetDefinition.OrderwireKillJsDb"/> above the control
    /// station's orderwire as received at the satellite (its <see cref="SatcomNetDefinition.OrderwireEirpDbw"/>
    /// from the sub-satellite point). Always false for dedicated waveforms. Returns the jammer most responsible.
    /// </summary>
    public static (bool Jammed, string? JammerId, string? JammerName) OrderwireJammed(
        IReadOnlyList<SatcomJammerSource> jammers, SatcomNetDefinition net, SatcomSatelliteDefinition satellite,
        SatcomSatellitePosition satPos)
    {
        if (!net.Waveform.IsDama() || jammers.Count == 0) return (false, null, null);
        var orderwireHz = net.OrderwireUplinkHz ?? net.UplinkHz;

        double total = 0.0, best = 0.0;
        SatcomJammerSource? dominant = null;
        foreach (var jammer in jammers)
        {
            if (UplinkJammerDbw(jammer, net, satellite, satPos, orderwireHz) is not { } dbw) continue;
            var w = Math.Pow(10.0, dbw / 10.0);
            total += w;
            if (w > best)
            {
                best = w;
                dominant = jammer;
            }
        }

        if (total <= 0.0 || dominant == null) return (false, null, null);

        var slant = Math.Max(1.0, satPos.AltitudeMeters);
        var orderwireDbw = net.OrderwireEirpDbw - SatcomLinkBudget.FreeSpacePathLossDb(slant, orderwireHz) +
                           satellite.Health * net.SatelliteEirpAdjustDb;
        var js = 10.0 * Math.Log10(total) - orderwireDbw;
        return js >= net.OrderwireKillJsDb ? (true, dominant.Id, dominant.DisplayName) : (false, null, null);
    }
}
