using System;

namespace OpenFreq.Common.Satcom;

/// <summary>
/// Stateless per-leg and combined-link evaluation -- the shared physics core used authoritatively
/// by the server's SatcomLinkEngine (real two-endpoint geometry, real ephemeris) and by the
/// client only for its own read-only debug-panel display of numbers the server already pushed
/// down. Every transmission evaluates ONE uplink leg (transmitter -&gt; satellite) and, separately,
/// ONE downlink leg per recipient (satellite -&gt; that recipient) -- never a single "distance
/// between the two aircraft" shortcut, since bent-pipe relay legs are independent.
/// </summary>
public static class SatcomLinkEngineCore
{
    /// <summary>Evaluates one leg (either direction -- caller supplies which frequency and which
    /// terminal is the "local" end of this leg; <paramref name="satPos"/> is always the other
    /// end). Includes elevation-mask, Earth-ellipsoid occlusion (distinct from any DCS local
    /// terrain LOS, which the caller applies separately -- see SatcomLinkEngine), footprint/antenna
    /// gain, FSPL, and C/N0.</summary>
    public static SatcomLegResult EvaluateLeg(SatcomNetDefinition net, SatcomSatelliteDefinition satellite,
        SatcomSatellitePosition satPos, SatcomTerminalState terminal, double frequencyHz, bool isUplinkLeg)
    {
        var satEcef = satPos.ToEcef();
        var look = SatcomGeodesy.LookAngles(terminal.LatitudeDeg, terminal.LongitudeDeg, terminal.AltitudeMeters,
            satEcef);

        if (look.ElevationDeg < net.MinElevationMaskDeg)
        {
            return new SatcomLegResult(look.ElevationDeg, look.AzimuthDeg, look.SlantRangeMeters, -100,
                0, -999, SatcomLinkBudget.PropagationDelaySeconds(look.SlantRangeMeters),
                AboveElevationMask: false, EarthOccluded: false,
                $"below horizon/mask ({look.ElevationDeg:F1}deg < {net.MinElevationMaskDeg:F1}deg)");
        }

        var terminalEcef = SatcomGeodesy.GeodeticToEcef(terminal.LatitudeDeg, terminal.LongitudeDeg,
            terminal.AltitudeMeters);
        var earthOccluded = SatcomGeodesy.EllipsoidOccludes(terminalEcef, satEcef);
        if (earthOccluded)
        {
            return new SatcomLegResult(look.ElevationDeg, look.AzimuthDeg, look.SlantRangeMeters, -100,
                0, -999, SatcomLinkBudget.PropagationDelaySeconds(look.SlantRangeMeters),
                AboveElevationMask: true, EarthOccluded: true, "Earth-ellipsoid horizon occludes satellite");
        }

        var footprintAngle = SatcomGeodesy.GeocentricAngleFromSubsatellite(
            satPos.LatitudeDeg, satPos.LongitudeDeg, terminal.LatitudeDeg, terminal.LongitudeDeg);
        var antennaGain = SatcomAntennaModel.Evaluate(look, footprintAngle,
            satellite.FootprintHalfPowerDeg, satellite.FootprintCutoffDeg,
            terminal.HeadingRad, terminal.PitchRad, terminal.BankRad, terminal.AntennaSelection);
        var antennaGainDb = antennaGain.CombinedGainDb;

        if (antennaGainDb <= -100.0)
        {
            return new SatcomLegResult(look.ElevationDeg, look.AzimuthDeg, look.SlantRangeMeters, antennaGainDb,
                0, -999, SatcomLinkBudget.PropagationDelaySeconds(look.SlantRangeMeters),
                AboveElevationMask: true, EarthOccluded: false,
                "antenna blocked (outside footprint or airframe-masked)",
                antennaGain.TiltDeg, antennaGain.OffBoresightDeg, antennaGain.FootprintGainDb, antennaGain.TerminalGainDb);
        }

        var pathLossDb = SatcomLinkBudget.FreeSpacePathLossDb(look.SlantRangeMeters, frequencyHz);
        var eirpAdjustDb = satellite.Health * net.SatelliteEirpAdjustDb;

        double receivedDbw;
        if (isUplinkLeg)
        {
            // Terminal Tx -> satellite Rx: terminal transmit power/antenna, satellite's lumped
            // EIRP-adjust stands in for the satellite receiver's own gain advantage.
            var txPowerDbw = 10.0 * Math.Log10(Math.Max(0.001, net.TxPowerWatts));
            receivedDbw = SatcomLinkBudget.ReceivedCarrierDbw(txPowerDbw, antennaGainDb, pathLossDb,
                eirpAdjustDb, net.ImplementationLossDb);
        }
        else
        {
            // Satellite Tx -> terminal Rx: satellite's lumped EIRP-adjust stands in for the
            // satellite transmitter, terminal antenna is the receive gain.
            var satEirpDbw = 10.0 * Math.Log10(Math.Max(0.001, net.TxPowerWatts)) + eirpAdjustDb;
            receivedDbw = SatcomLinkBudget.ReceivedCarrierDbw(satEirpDbw, 0.0, pathLossDb,
                antennaGainDb, net.ImplementationLossDb);
        }

        var noiseDensityDbwPerHz = SatcomLinkBudget.NoiseDensityDbwPerHz(net.SystemNoiseTemperatureK);
        var cn0DbHz = SatcomLinkBudget.CarrierToNoiseDensityDbHz(receivedDbw, noiseDensityDbwPerHz);
        var propagationSeconds = SatcomLinkBudget.PropagationDelaySeconds(look.SlantRangeMeters);

        return new SatcomLegResult(look.ElevationDeg, look.AzimuthDeg, look.SlantRangeMeters, antennaGainDb,
            pathLossDb, cn0DbHz, propagationSeconds, AboveElevationMask: true, EarthOccluded: false, "",
            antennaGain.TiltDeg, antennaGain.OffBoresightDeg, antennaGain.FootprintGainDb, antennaGain.TerminalGainDb);
    }

    /// <summary>Combines an already-evaluated uplink and downlink leg into the final link result:
    /// linear C/N0 combining, Eb/N0, modulation BER, FEC, and frame error rate.</summary>
    public static SatcomLinkResult Combine(string satelliteId, string satelliteName,
        SatcomLegResult uplink, SatcomLegResult downlink, SatcomNetDefinition net,
        SatcomLinkQualityState qualityState)
    {
        if (!uplink.Usable)
            return SatcomLinkResult.Unavailable(SatcomAcquisitionFailureReason.BelowHorizon, uplink.UnavailableReason)
                with { Uplink = uplink, Downlink = downlink };
        if (!downlink.Usable)
            return SatcomLinkResult.Unavailable(SatcomAcquisitionFailureReason.BelowHorizon, downlink.UnavailableReason)
                with { Uplink = uplink, Downlink = downlink };

        var combinedCn0DbHz = SatcomLinkBudget.CombineCarrierToNoiseDensityDbHz(uplink.Cn0DbHz, downlink.Cn0DbHz);
        var ebN0Db = SatcomLinkBudget.EbN0Db(combinedCn0DbHz, net.BitRateBps);
        var postFecBer = SatcomLinkBudget.PostFecBer(ebN0Db, net.FecCodingGainDb, net.FecStrength, net.Modulation);
        var rawBer = SatcomLinkBudget.ModulationBer(ebN0Db, net.Modulation);
        var fer = SatcomLinkBudget.FrameErrorRate(postFecBer, net.FrameBits);
        var totalPropagation = uplink.PropagationSeconds + downlink.PropagationSeconds;

        return new SatcomLinkResult
        {
            SatelliteId = satelliteId,
            SatelliteName = satelliteName,
            Uplink = uplink,
            Downlink = downlink,
            CombinedCn0DbHz = combinedCn0DbHz,
            EbN0Db = ebN0Db,
            RawBer = rawBer,
            PostFecBer = postFecBer,
            FrameErrorRate = fer,
            PropagationLatencySeconds = totalPropagation,
            QualityState = qualityState,
            LinkAvailable = qualityState != SatcomLinkQualityState.Lost,
            FailureReason = qualityState == SatcomLinkQualityState.Lost
                ? SatcomAcquisitionFailureReason.InsufficientAcquisitionMargin
                : SatcomAcquisitionFailureReason.None,
            UnavailableReason = ""
        };
    }
}
