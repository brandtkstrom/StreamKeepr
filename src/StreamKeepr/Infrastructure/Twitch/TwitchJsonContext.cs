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
[JsonSerializable(typeof(EventSubSubscriptionRequest))]
[JsonSerializable(typeof(ConduitState))]
[JsonSerializable(typeof(HelixEventSubSubscriptionsResponse))]
[JsonSerializable(typeof(HelixConduitsResponse))]
[JsonSerializable(typeof(CreateConduitRequest))]
[JsonSerializable(typeof(UpdateConduitShardsRequest))]
[JsonSerializable(typeof(UpdateConduitShardsResponse))]
[JsonSerializable(typeof(HelixErrorResponse))]
internal partial class TwitchJsonContext : JsonSerializerContext
{
}
