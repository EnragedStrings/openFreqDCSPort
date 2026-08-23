using System.Text.Json.Serialization;

namespace OpenFreqServer.SrsBridge;

/// <summary>
/// Wire-format DTOs for DCS-SimpleRadioStandalone's (SRS) TCP sync protocol -- newline-delimited
/// JSON over the same TCP+UDP port (default 5002). Field/property names and enum ordinal values
/// are PROJECT_OBSERVED, copied field-for-field from the real, MIT-licensed
/// ciribob/DCS-SimpleRadioStandalone source (Common/Models/NetworkMessage.cs,
/// Common/Models/Player/*.cs, read directly 2026-08-22) so an unmodified real SRS client can
/// deserialize what this bridge sends and vice versa. Do not rename/reorder anything here without
/// re-checking against that source -- SRS uses System.Text.Json's default (numeric) enum
/// serialization with no [JsonStringEnumConverter], and mixes PascalCase properties with
/// lowercase-named fields/properties depending on the original C# member style, so casing here is
/// deliberate per-member, not a blanket naming policy.
/// </summary>
public class SrsNetworkMessage
{
    public enum MessageType
    {
        UPDATE,
        PING,
        SYNC,
        RADIO_UPDATE,
        SERVER_SETTINGS,
        CLIENT_DISCONNECT,
        VERSION_MISMATCH,
        EXTERNAL_AWACS_MODE_PASSWORD,
        EXTERNAL_AWACS_MODE_DISCONNECT,
        GATEWAY_CLIENT_FULL_UPDATE,
        GATEWAY_CLIENT_METADATA_UPDATE,
        GATEWAY_CLIENT_DISCONNECT
    }

    [JsonPropertyName("Client")]
    public SrsClient? Client { get; set; }

    [JsonPropertyName("MsgType")]
    public MessageType MsgType { get; set; }

    [JsonPropertyName("Clients")]
    public List<SrsClient>? Clients { get; set; }

    [JsonPropertyName("ServerSettings")]
    public Dictionary<string, string>? ServerSettings { get; set; }

    [JsonPropertyName("ExternalAWACSModePassword")]
    public string? ExternalAWACSModePassword { get; set; }

    [JsonPropertyName("Version")]
    public string? Version { get; set; }
}

public class SrsClient
{
    [JsonPropertyName("ClientGuid")]
    public string ClientGuid { get; set; } = "";

    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("Coalition")]
    public int Coalition { get; set; }

    [JsonPropertyName("AllowRecord")]
    public bool AllowRecord { get; set; }

    [JsonPropertyName("Seat")]
    public int Seat { get; set; }

    [JsonPropertyName("RadioInfo")]
    public SrsPlayerRadioInfo? RadioInfo { get; set; }

    [JsonPropertyName("LatLngPosition")]
    public SrsLatLngPosition? LatLngPosition { get; set; }

    [JsonPropertyName("Gateway")]
    public bool Gateway { get; set; }

    [JsonPropertyName("DISEntityId")]
    public int DISEntityId { get; set; } = -1;

    [JsonPropertyName("GatewayClient")]
    public bool GatewayClient { get; set; }
}

public class SrsLatLngPosition
{
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lng")]
    public double Lng { get; set; }

    [JsonPropertyName("alt")]
    public double Alt { get; set; }
}

public class SrsPlayerRadioInfo
{
    /// <summary>Fixed 11 slots (10 radios + intercom), Constants.MAX_RADIOS in the real client.
    /// Always sent at exactly this length -- SRS clients index into it positionally.</summary>
    public const int MaxRadios = 11;

    [JsonPropertyName("ambient")]
    public SrsAmbient Ambient { get; set; } = new();

    [JsonPropertyName("iff")]
    public SrsTransponder Iff { get; set; } = new();

    [JsonPropertyName("radios")]
    public SrsRadio[] Radios { get; set; } = CreateDefaultRadios();

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "";

    [JsonPropertyName("unitId")]
    public uint UnitId { get; set; }

    public static SrsRadio[] CreateDefaultRadios()
    {
        var radios = new SrsRadio[MaxRadios];
        for (var i = 0; i < radios.Length; i++) radios[i] = new SrsRadio();
        return radios;
    }
}

public class SrsAmbient
{
    [JsonPropertyName("abType")]
    public string AbType { get; set; } = "";

    [JsonPropertyName("vol")]
    public float Vol { get; set; }
}

public class SrsTransponder
{
    public enum IffStatus
    {
        OFF,
        NORMAL,
        IDENT
    }

    [JsonPropertyName("Mode1")]
    public int Mode1 { get; set; } = -1;

    [JsonPropertyName("Mode3")]
    public int Mode3 { get; set; } = -1;

    [JsonPropertyName("Mode4")]
    public bool Mode4 { get; set; }

    [JsonPropertyName("Status")]
    public IffStatus Status { get; set; } = IffStatus.OFF;
}

/// <summary>AM/FM/etc modulation. Ordinal values match SRS's Common/Models/Player/Modulation.cs
/// exactly -- serialized as a plain integer (no string converter).</summary>
public enum SrsModulation
{
    AM,
    FM,
    INTERCOM,
    DISABLED,
    HAVEQUICK,
    SATCOM,
    MIDS,
    SINCGARS
}

public class SrsRadio
{
    [JsonPropertyName("enc")]
    public bool Enc { get; set; }

    [JsonPropertyName("encKey")]
    public byte EncKey { get; set; }

    [JsonPropertyName("freq")]
    public double Freq { get; set; } = 1;

    [JsonPropertyName("modulation")]
    public SrsModulation Modulation { get; set; } = SrsModulation.DISABLED;

    [JsonPropertyName("retransmit")]
    public bool Retransmit { get; set; }

    [JsonPropertyName("secFreq")]
    public double SecFreq { get; set; } = 1;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("Model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("IntercomUnitId")]
    public uint IntercomUnitId { get; set; }

    /// <summary>Real radios tune above 10kHz; below that is SRS's own sentinel for "not tuned".
    /// Mirrors PlayerRadioInfoBase.CanHearTransmission's own freq &gt; 10000 gate.</summary>
    public bool IsTuned => Freq > 10_000 && Modulation != SrsModulation.DISABLED;
}
