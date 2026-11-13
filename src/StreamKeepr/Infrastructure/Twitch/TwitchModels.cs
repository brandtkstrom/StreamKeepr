using System.Text.Json.Serialization;

namespace StreamKeepr.Infrastructure.Twitch;

internal sealed record TwitchTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

internal sealed record HelixStreamsResponse(
    [property: JsonPropertyName("data")] List<HelixStream>? Data);

internal sealed record HelixStream(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("user_login")] string UserLogin,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("game_name")] string GameName,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("thumbnail_url")] string? ThumbnailUrl);

internal sealed record HelixChannelsResponse(
    [property: JsonPropertyName("data")] List<HelixChannel>? Data);

internal sealed record HelixChannel(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("game_name")] string GameName);

internal sealed record HelixUsersResponse(
    [property: JsonPropertyName("data")] List<HelixUser>? Data);

internal sealed record HelixUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("profile_image_url")] string? ProfileImageUrl);

internal sealed record EventSubSubscriptionRequest(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("condition")] EventSubCondition Condition,
    [property: JsonPropertyName("transport")] EventSubTransport Transport);

// Null when a subscription type has no broadcaster condition (for example conduit.shard.disabled).
internal sealed record EventSubCondition(
    [property: JsonPropertyName("broadcaster_user_id")] string? BroadcasterUserId);

// "websocket" carries session_id; "conduit" carries conduit_id.
internal sealed record EventSubTransport(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("session_id")] string? SessionId = null,
    [property: JsonPropertyName("conduit_id")] string? ConduitId = null);

internal sealed record HelixEventSubSubscriptionsResponse(
    [property: JsonPropertyName("data")] List<HelixEventSubSubscription>? Data,
    [property: JsonPropertyName("total_cost")] int TotalCost,
    [property: JsonPropertyName("max_total_cost")] int MaxTotalCost,
    [property: JsonPropertyName("pagination")] HelixPagination? Pagination);

internal sealed record HelixEventSubSubscription(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("condition")] EventSubCondition? Condition,
    [property: JsonPropertyName("transport")] EventSubTransport? Transport);

internal sealed record HelixPagination(
    [property: JsonPropertyName("cursor")] string? Cursor);

internal sealed record HelixConduitsResponse(
    [property: JsonPropertyName("data")] List<HelixConduit>? Data);

internal sealed record HelixConduit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("shard_count")] int ShardCount);

internal sealed record CreateConduitRequest(
    [property: JsonPropertyName("shard_count")] int ShardCount);

internal sealed record UpdateConduitShardsRequest(
    [property: JsonPropertyName("conduit_id")] string ConduitId,
    [property: JsonPropertyName("shards")] List<ConduitShardUpdate> Shards);

internal sealed record ConduitShardUpdate(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("transport")] EventSubTransport Transport);

internal sealed record UpdateConduitShardsResponse(
    [property: JsonPropertyName("errors")] List<ConduitShardError>? Errors);

internal sealed record ConduitShardError(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("code")] string? Code);

internal sealed record HelixErrorResponse(
    [property: JsonPropertyName("message")] string? Message);
