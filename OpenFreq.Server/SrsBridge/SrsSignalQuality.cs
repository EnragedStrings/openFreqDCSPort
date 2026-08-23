using OpenFreqAudio;

namespace OpenFreqServer.SrsBridge;

/// <summary>
/// Computes AudioParams for a leg involving an SRS-bridged peer, purely from geodetic distance --
/// see FreeSpacePathModel's own doc comment. No terrain input here at all (the bridge has no map
/// data of its own); real terrain LOS for SRS legs is the remote-oracle mechanism from phase
/// 4b-4d of the SRS bridge plan, layered on top of this once it exists, not a replacement for it.
/// </summary>
public static class SrsSignalQuality
{
    // Same fallback sensitivity constants as OpenFreqService.GetReceiverSensitivityDb's own
    // no-RadioStationData branch -- the bridge has no per-radio preset for an SRS receiver either.
    private const double VhfSensitivityDb = -113.0;
    private const double UhfSensitivityDb = -107.0;

    /// <summary>Builds AudioParams for one SRS receiver hearing one OpenFreq transmitter, from
    /// each side's geodetic position. Falls back to FastPathAudioSim.GetDefaultAudioParams (clear,
    /// no degradation) when either side's position is unknown -- an unresolved position is not
    /// evidence of a bad link, just missing data, so this deliberately doesn't guess worst-case.</summary>
    public static AudioParams CreateAudioParams(int frequencyKhz, double txPowerWatts, double ppm,
        double? txLatitudeDeg, double? txLongitudeDeg, double? txAltitudeMeters,
        double? rxLatitudeDeg, double? rxLongitudeDeg, double? rxAltitudeMeters)
    {
        if (txLatitudeDeg == null || txLongitudeDeg == null || txAltitudeMeters == null ||
            rxLatitudeDeg == null || rxLongitudeDeg == null || rxAltitudeMeters == null)
        {
            return FastPathAudioSim.GetDefaultAudioParams(frequencyKhz, (float)ppm);
        }

        var distanceMeters = FreeSpacePathModel.GreatCircleDistanceMeters(
            txLatitudeDeg.Value, txLongitudeDeg.Value, txAltitudeMeters.Value,
            rxLatitudeDeg.Value, rxLongitudeDeg.Value, rxAltitudeMeters.Value);

        var receiverSensitivityDb = RadioStationPreset.IsVHF(frequencyKhz) ? VhfSensitivityDb : UhfSensitivityDb;

        return FreeSpacePathModel.CreateAudioParams(frequencyKhz, txPowerWatts, ppm, distanceMeters,
            txAltitudeMeters.Value, rxAltitudeMeters.Value, receiverSensitivityDb);
    }
}
