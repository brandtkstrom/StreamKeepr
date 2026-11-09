using StreamRecorder.Config;
using StreamRecorder.Twitch;

namespace StreamRecorder.Domain;

public interface IStreamInfoUpdater
{
    Task<StreamInfo?> RefreshAsync(CancellationToken cancellationToken = default);
}

public class StreamInfoUpdater : IStreamInfoUpdater
{
    private readonly AppConfig _appConfig;
    private readonly IStreamInfoAccessor _streamInfo;
    private readonly ITwitchApiClient _twitchApi;
    private readonly ILogger<StreamInfoUpdater> _logger;

    public StreamInfoUpdater(AppConfig appConfig, IStreamInfoAccessor streamInfo, ITwitchApiClient twitchApi, ILogger<StreamInfoUpdater> logger)
    {
        _appConfig = appConfig;
        _streamInfo = streamInfo;
        _twitchApi = twitchApi;
        _logger = logger;
    }

    public async Task<StreamInfo?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var userLogin = _appConfig.TwitchOptions.UserLogin;

        _logger.LogDebug("Fetching stream info for user {UserLogin}", userLogin);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var streamInfo = await _twitchApi.GetStreamInfoAsync(userLogin, cancellationToken);

            _streamInfo.Set(streamInfo);

            _logger.LogDebug("User {UserLogin} stream info refresh complete", userLogin);

            return streamInfo;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error fetching stream info");
        }

        return null;
    }
}