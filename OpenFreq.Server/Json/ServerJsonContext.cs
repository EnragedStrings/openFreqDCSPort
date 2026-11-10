using System.Text.Json.Serialization;
using OpenFreq.Common;
using OpenFreq.Common.Signaling;

namespace OpenFreqServer.Json;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ServerConfig))]
[JsonSerializable(typeof(JammingServerConfig))]
[JsonSerializable(typeof(OpenFreq.Common.Eccm.EccmFill))]
public partial class ServerJsonContext : JsonSerializerContext
{
}
