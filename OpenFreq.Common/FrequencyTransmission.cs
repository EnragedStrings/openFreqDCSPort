using System.Text.Json.Serialization;
using OpenFreqAudio;

namespace OpenFreq.Common;

/// <summary>
/// Represents transmission state for a single frequency
/// </summary>
public class FrequencyTransmission
{
    /// <summary>
    /// Frequency in KHz
    /// </summary>
    [JsonPropertyName("khz")]
    public int Khz { get; set; }
    [JsonPropertyName("txpower")]
    public double TxPowerWatts { get; set; }

    [JsonPropertyName("position")]
    public Vector3? Position { get; set; }

    [JsonPropertyName("dcsPosition")]
    public Vector3? DcsPosition { get; set; }

    [JsonPropertyName("velocity")]
    public Vector3? Velocity { get; set; }

    [JsonPropertyName("in3d")]
    public bool In3d { get; set; }

    [JsonPropertyName("ppm")]
    public double Ppm { get; set; }

    [JsonPropertyName("ambient")]
    public AmbientNoiseType AmbientNoiseType { get; set; }

    /// <summary>KY-58/COMSEC encryption engaged on the transmitting radio.</summary>
    [JsonPropertyName("enc")]
    public bool Enc { get; set; }

    /// <summary>Encryption key channel (1-6). 0 = none.</summary>
    [JsonPropertyName("encKey")]
    public int EncKey { get; set; }

    /// <summary>HAVE QUICK frequency-hopping engaged on the transmitting radio.</summary>
    [JsonPropertyName("hqOn")]
    public bool HqOn { get; set; }

    /// <summary>Geodetic position of the transmitter, when known. Unlike <see cref="DcsPosition"/>
    /// (DCS mission-local X/Y/Z, only meaningful within one live DCS instance's own coordinate
    /// frame), these are plain lat/lon/alt -- comparable across any two transmitters regardless of
    /// whether either side has a DCS export at all, which is what makes them usable for an
    /// SRS-bridged peer (SRS reports its own position this way; the bridge has no DCS-local frame
    /// to convert into). Null when the sender has no position fix at all.</summary>
    [JsonPropertyName("lat")]
    public double? LatitudeDeg { get; set; }

    [JsonPropertyName("lon")]
    public double? LongitudeDeg { get; set; }

    [JsonPropertyName("alt")]
    public double? AltitudeMeters { get; set; }

    public FrequencyTransmission()
    {
    }

    public FrequencyTransmission(int khz, double txPowerWatts, double ppm, Vector3? position, Vector3? velocity,
        bool in3d, AmbientNoiseType ambientNoiseType = AmbientNoiseType.None, Vector3? dcsPosition = null,
        bool enc = false, int encKey = 0, bool hqOn = false, double? latitudeDeg = null,
        double? longitudeDeg = null, double? altitudeMeters = null)
    {
        Khz = khz;
        TxPowerWatts = txPowerWatts;
        Ppm = ppm;
        Position = position;
        DcsPosition = dcsPosition;
        Velocity = velocity;
        In3d = in3d;
        AmbientNoiseType = ambientNoiseType;
        Enc = enc;
        EncKey = encKey;
        HqOn = hqOn;
        LatitudeDeg = latitudeDeg;
        LongitudeDeg = longitudeDeg;
        AltitudeMeters = altitudeMeters;
    }
}
