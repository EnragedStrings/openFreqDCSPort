namespace OpenFreqAudio;

/// <summary>
/// Geometric (no-terrain) signal quality model: free-space path loss plus a horizon-distance
/// rolloff, extracted from OpenFreqService's DCS-mode fallback so it can be shared by any caller
/// that only has two positions and no live DCS terrain data to raycast against -- the OpenFreq
/// client's own no-terrain fallback, and the SRS bridge (which never has terrain data at all; see
/// SrsSignalQuality.cs). Works purely in distanceMeters/altitude terms so callers can feed it
/// either DCS mission-local Euclidean distance or geodetic great-circle distance -- the FSPL/
/// horizon math itself doesn't care which coordinate system produced the distance.
/// </summary>
public static class FreeSpacePathModel
{
    private const double EarthRadiusMeters = 6378000.0;
    private const double SpeedOfLightMetersPerSecond = 299792458.0;

    /// <summary>Great-circle surface distance (haversine) combined with the altitude difference via
    /// Pythagoras -- accurate enough for radio-range distances (tens to low hundreds of km), where
    /// the earth's curvature over the vertical delta is negligible.</summary>
    public static double GreatCircleDistanceMeters(double lat1Deg, double lon1Deg, double alt1Meters,
        double lat2Deg, double lon2Deg, double alt2Meters)
    {
        var lat1 = DegreesToRadians(lat1Deg);
        var lat2 = DegreesToRadians(lat2Deg);
        var dLat = DegreesToRadians(lat2Deg - lat1Deg);
        var dLon = DegreesToRadians(lon2Deg - lon1Deg);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        var surfaceDistance = EarthRadiusMeters * c;

        var altDelta = alt2Meters - alt1Meters;
        return Math.Sqrt(surfaceDistance * surfaceDistance + altDelta * altDelta);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    /// <summary>Base free-space signal estimate from distance alone -- FSPL + a small fixed
    /// weather-loss term, no horizon/extra-distance loss (that's the separate <see
    /// cref="ApplyHorizonLoss"/> stage, kept apart so callers that already have a real
    /// terrain/signal calculation for the base signal can still layer horizon loss on top of it
    /// without double-computing FSPL). Mirrors OpenFreqService.TryCreateDcsFreeSpaceAudioParams
    /// (OpenFreqService.cs:1815-1843), generalized off Vector3 positions onto a plain distance.</summary>
    public static AudioParams CreateBaseAudioParams(int frequencyKhz, double txPowerWatts, double ppm,
        double distanceMeters, double receiverSensitivityDb)
    {
        var clampedDistance = Math.Max(1.0, distanceMeters);
        var frequencyHz = Math.Max(1.0, frequencyKhz * 1000.0);
        var txPowerDbm = 10.0 * Math.Log10(Math.Max(0.001, txPowerWatts) * 1000.0);
        var fsplDb = 20.0 * Math.Log10(4.0 * Math.PI * clampedDistance * frequencyHz / SpeedOfLightMetersPerSecond);
        var weatherLossDb = 0.02 * (clampedDistance / 1000.0);

        var audioParams = FastPathAudioSim.GetDefaultAudioParams(frequencyKhz, (float)ppm);
        audioParams.ReceivedDb = (float)(txPowerDbm - fsplDb - weatherLossDb);
        audioParams.ReceivedSnrDb = audioParams.ReceivedDb - (float)receiverSensitivityDb;
        UpdateFadingRates(audioParams);
        return audioParams;
    }

    /// <summary>One-shot convenience combining <see cref="CreateBaseAudioParams"/> and <see
    /// cref="ApplyHorizonLoss"/> for callers (the SRS bridge) that have no existing base signal
    /// calculation of their own to layer onto -- see OpenFreqService.PrepareReceivedAudioParams
    /// for the two-stage form this mirrors when a caller (the OpenFreq client) does.</summary>
    public static AudioParams CreateAudioParams(int frequencyKhz, double txPowerWatts, double ppm,
        double distanceMeters, double txAltitudeMeters, double rxAltitudeMeters, double receiverSensitivityDb)
    {
        var audioParams = CreateBaseAudioParams(frequencyKhz, txPowerWatts, ppm, distanceMeters,
            receiverSensitivityDb);
        ApplyHorizonLoss(audioParams, distanceMeters, txAltitudeMeters, rxAltitudeMeters);
        return audioParams;
    }

    /// <summary>Distance-band extra loss plus a horizon-distance rolloff/block once the two
    /// stations are far enough apart that the earth's curvature alone would occlude them, applied
    /// on top of whatever base signal the caller already computed. Mirrors OpenFreqService.
    /// ApplyDcsDistanceAndHorizonLoss (OpenFreqService.cs:1845-1894), generalized off Vector3
    /// positions onto plain distance/altitude inputs.</summary>
    public static void ApplyHorizonLoss(AudioParams audioParams, double distanceMeters,
        double txAltitudeMeters, double rxAltitudeMeters)
    {
        var distanceNm = distanceMeters / 1852.0;

        var extraLossDb = 0.0;
        if (distanceNm > 60.0)
            extraLossDb += (Math.Min(distanceNm, 140.0) - 60.0) * 0.10;
        if (distanceNm > 140.0)
            extraLossDb += (Math.Min(distanceNm, 220.0) - 140.0) * 0.25;
        if (distanceNm > 220.0)
            extraLossDb += (distanceNm - 220.0) * 0.75;

        var horizonMeters = FastPathAudioSim.CalculateRadioHorizonMeters(txAltitudeMeters, rxAltitudeMeters);
        if (horizonMeters > 1.0)
        {
            var horizonRatio = distanceMeters / horizonMeters;
            if (horizonRatio > 0.75)
            {
                var nearHorizon = Math.Min(horizonRatio, 1.0) - 0.75;
                extraLossDb += Math.Pow(nearHorizon / 0.25, 2.0) * 12.0;
            }

            if (horizonRatio > 1.0)
                extraLossDb += 24.0 + (horizonRatio - 1.0) * 80.0;

            if (horizonRatio > 1.15)
                audioParams.SignalBlocked = true;
        }

        if (extraLossDb > 0.001)
        {
            audioParams.ReceivedDb -= (float)extraLossDb;
            audioParams.ReceivedSnrDb -= (float)extraLossDb;
            UpdateFadingRates(audioParams);
        }

        if (audioParams.ReceivedSnrDb <= -18.0f)
            audioParams.SignalBlocked = true;
    }

    private static void UpdateFadingRates(AudioParams audioParams)
    {
        var bandConfig = FastPathAudioSim.GetBandConfig(audioParams.RadioFrequencyKHz);
        audioParams.DropoutRate = FastPathAudioSim.CalculateDropoutRate(audioParams.ReceivedSnrDb, bandConfig);
        audioParams.DeepFadeRate = FastPathAudioSim.CalculateDeepFadeRate(audioParams.ReceivedSnrDb, bandConfig);
    }
}
