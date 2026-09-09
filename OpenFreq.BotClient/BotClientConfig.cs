using System.Text.Json.Serialization;

namespace OpenFreq.BotClient;

/// <summary>
/// One bot process's config: a server to connect to, and a list of named, independently-
/// positioned "controllers" it operates simultaneously over a single connection -- e.g. a
/// "Nellis Tower" on one frequency and a "Luke Tower" on another, both live at once. This is the
/// exact same "one connection, many positions" pattern GCI mode's own multi-location UI already
/// uses -- see the README's Bot Clients section.
/// </summary>
public class BotClientConfig
{
    [JsonPropertyName("serverAddress")] public string ServerAddress { get; set; } = "127.0.0.1:9987";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "OpenFreq Bot";
    [JsonPropertyName("positions")] public List<BotPositionConfig> Positions { get; set; } = [];
}

/// <summary>One named, independently-positioned "controller" this bot operates. Position is
/// declared to the server (JoinChannelMessage.Lat/Lon/Alt) so transcript delivery can be gated by
/// real line-of-sight/audibility, same as any other listener -- see TranscriptDeliveryMessage's
/// own doc comment.</summary>
public class BotPositionConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = "Unnamed Position";
    [JsonPropertyName("frequencyKhz")] public int FrequencyKhz { get; set; }
    [JsonPropertyName("lat")] public double Lat { get; set; }
    [JsonPropertyName("lon")] public double Lon { get; set; }
    [JsonPropertyName("alt")] public double Alt { get; set; }

    /// <summary>Optional path to a WAV file (any sample rate/mono or stereo, 16-bit PCM) to
    /// transmit back when this position receives a transcript -- proving the "respond" half of
    /// the loop. When null/missing, a short synthesized acknowledgment tone is used instead, so
    /// the demo runs with zero audio assets required.</summary>
    [JsonPropertyName("responseWavPath")] public string? ResponseWavPath { get; set; }
}
