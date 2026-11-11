using StreamRecorder.Domain;

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

            var response = await _httpClient.GetAsync(uri, TwitchJsonContext.Default.GetStreamsResponse, cancelToken);

            if (!string.IsNullOrWhiteSpace(response.Error))
            {
                _logger.LogWarning(response.Error);
            }

            var liveStream = response.Data?.Streams.FirstOrDefault(s => s.Type.Equals("live", StringComparison.OrdinalIgnoreCase));

            return MapStreamInfo(liveStream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching stream info");
        }

        return null;
    }

    private static StreamInfo? MapStreamInfo(TwitchStreamDto? dto)
    {
        return dto is not null
                   ? new StreamInfo(dto.Id,
                                    dto.UserLogin,
                                    dto.UserName,
                                    dto.Title,
                                    dto.StartedAt ?? DateTimeOffset.UtcNow)
                   : null;
    }
}
