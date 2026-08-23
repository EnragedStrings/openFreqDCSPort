using System.Text.Json.Serialization;

namespace OpenFreqServer.SrsBridge;

/// <summary>
/// Source-gen JSON context for the SRS wire protocol (<see cref="SrsSyncProtocol"/>). No naming
/// policy -- casing is explicit per-member via [JsonPropertyName] to match SRS's own mixed
/// PascalCase/lowercase wire format exactly (see SrsSyncProtocol.cs's doc comment).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SrsNetworkMessage))]
[JsonSerializable(typeof(SrsClient))]
[JsonSerializable(typeof(SrsPlayerRadioInfo))]
[JsonSerializable(typeof(SrsRadio))]
[JsonSerializable(typeof(SrsLatLngPosition))]
[JsonSerializable(typeof(SrsAmbient))]
[JsonSerializable(typeof(SrsTransponder))]
public partial class SrsJsonContext : JsonSerializerContext
{
}
