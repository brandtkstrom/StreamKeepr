using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamRecorder.Twitch;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
                             PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
                             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                             UseStringEnumConverter = true)]
[JsonSerializable(typeof(GetTokenDto))]
[JsonSerializable(typeof(GetStreamsDto))]
[JsonSerializable(typeof(List<StreamInfo>))]
[JsonSerializable(typeof(StreamInfo))]
internal partial class TwitchJsonContext : JsonSerializerContext { }