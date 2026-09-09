using System.Text.Json.Serialization;

namespace OpenFreq.BotClient;

[JsonSerializable(typeof(BotClientConfig))]
internal partial class BotClientJsonContext : JsonSerializerContext;
