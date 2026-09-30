using System.Text.Json;
using System.Text.Json.Serialization;
using StreamRecorder.Core;

namespace StreamRecorder.Twitch;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
                             PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
                             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                             UseStringEnumConverter = true)]
[JsonSerializable(typeof(GetTokenDto))]
[JsonSerializable(typeof(GetStreamsDto))]
[JsonSerializable(typeof(List<TwitchStreamDto>))]
[JsonSerializable(typeof(TwitchStreamDto))]
internal partial class TwitchJsonContext : JsonSerializerContext { }