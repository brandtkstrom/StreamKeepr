using System.Text.Json.Serialization;
using LanguageExt;
using StreamRecorder.Core;

namespace StreamRecorder.Twitch;

public interface ITwitchApiClient
{
    Task<Option<StreamInfo>> GetStreamInfoAsync(string channelName, CancellationToken cancelToken = default);
}

public class TwitchApiClient : ITwitchApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TwitchApiClient> _logger;

    public TwitchApiClient(HttpClient httpClient, ILogger<TwitchApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<Option<StreamInfo>> GetStreamInfoAsync(string channelName, CancellationToken cancelToken = default)
    {
        try
        {
            var uri = $"/helix/streams?user_login={channelName}&first=1";
            await using var responseStream = await _httpClient.GetStreamAsync(uri, cancelToken);

            var response = await _httpClient.GetAsync(uri, TwitchJsonContext.Default.GetStreamsDto, cancelToken);

            if (!string.IsNullOrWhiteSpace(response.Error))
            {
                _logger.LogWarning(response.Error);
            }

            Option<TwitchStreamDto> streamInfoAsync = response.Data?.Streams.FirstOrDefault();

            return streamInfoAsync.ToStreamInfo();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching stream info");
        }

        return default;
    }
}

public enum StreamStatus
{
    Offline, Live
}

public sealed record TwitchStreamDto
{
    public string Id { get; init; } = string.Empty;

    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("user_login")]
    public string Channel { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string GameId { get; init; } = string.Empty;

    public string GameName { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public StreamStatus Status { get; init; } = StreamStatus.Offline;

    public string Title { get; init; } = string.Empty;

    public List<string> Tags { get; init; } = new();

    public int ViewerCount { get; init; }

    public DateTimeOffset? StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public string Language { get; init; } = string.Empty;

    public string ThumbnailUrl { get; init; } = string.Empty;

    public List<string> TagIds { get; init; } = new();

    public bool IsMature { get; init; }
}

public sealed record GetStreamsDto
{
    [JsonPropertyName("data")]
    public List<TwitchStreamDto> Streams { get; init; } = new();
}