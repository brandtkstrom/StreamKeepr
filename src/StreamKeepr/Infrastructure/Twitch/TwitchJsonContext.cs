using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamKeepr.Infrastructure.Twitch;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TwitchTokenResponse))]
[JsonSerializable(typeof(HelixStreamsResponse))]
[JsonSerializable(typeof(HelixChannelsResponse))]
[JsonSerializable(typeof(HelixUsersResponse))]
internal partial class TwitchJsonContext : JsonSerializerContext
{
}
