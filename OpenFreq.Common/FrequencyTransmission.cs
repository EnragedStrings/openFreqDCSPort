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

    public FrequencyTransmission()
    {
    }

    public FrequencyTransmission(int khz, double txPowerWatts, double ppm, Vector3? position, Vector3? velocity,
        bool in3d, AmbientNoiseType ambientNoiseType = AmbientNoiseType.None, Vector3? dcsPosition = null,
        bool enc = false, int encKey = 0, bool hqOn = false)
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
    }
}
