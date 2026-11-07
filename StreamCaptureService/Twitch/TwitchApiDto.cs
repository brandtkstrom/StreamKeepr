using System.Text.Json.Serialization;

namespace StreamRecorder.Twitch;

public sealed record GetTokenDto(
    [property: JsonPropertyName("access_token")]
    string AccessToken,
    [property: JsonPropertyName("expires_in")]
    int ExpiresIn,
    [property: JsonPropertyName("token_type")]
    string TokenType);

internal sealed record TwitchStreamDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("user_login")]
    string UserLogin,
    [property: JsonPropertyName("user_name")]
    string UserName,
    [property: JsonPropertyName("game_name")]
    string GameName,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("started_at")]
    DateTimeOffset? StartedAt);

internal sealed record GetStreamsResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<TwitchStreamDto> Streams);