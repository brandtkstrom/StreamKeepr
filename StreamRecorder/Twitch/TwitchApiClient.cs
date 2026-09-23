using System.Text.Json.Serialization;

namespace StreamRecorder.Twitch;

public interface ITwitchApiClient
{
    Task<StreamInfo?> GetStreamInfoAsync(string channelName, CancellationToken cancelToken = default);
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

    public async Task<StreamInfo?> GetStreamInfoAsync(string channelName, CancellationToken cancelToken = default)
    {
        try
        {
            var uri = $"/helix/streams?user_login={channelName}&first=1";
            await using var responseStream = await _httpClient.GetStreamAsync(uri, cancelToken);

            var response = await _httpClient.GetAsync(uri, TwitchJsonContext.Default.GetStreamsDto, cancelToken);

            if (!string.IsNullOrWhiteSpace(response.Error))
                _logger.LogWarning(response.Error);

            return response.Data?.Streams.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching stream info");
        }

        return null;
    }
}

public sealed record GetStreamsDto
{
    [JsonPropertyName("data")]
    public List<StreamInfo> Streams { get; init; } = new();
}