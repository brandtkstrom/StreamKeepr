using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamRecorder.Twitch;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
                             PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
                             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                             UseStringEnumConverter = true)]
[JsonSerializable(typeof(GetTokenDto))]
[JsonSerializable(typeof(TwitchStreamDto))]
[JsonSerializable(typeof(GetStreamsResponse))]
[JsonSerializable(typeof(IReadOnlyList<TwitchStreamDto>))]
internal partial class TwitchJsonContext : JsonSerializerContext { }