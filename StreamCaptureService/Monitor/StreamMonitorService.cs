using StreamRecorder.Config;
using StreamRecorder.Domain;

namespace StreamRecorder.Monitor;

public class StreamMonitorService : BackgroundService
{
    private readonly AppConfig _appConfig;
    private readonly IStreamInfoUpdater _streamInfoUpdater;
    private readonly ILogger<StreamMonitorService> _logger;

    public StreamMonitorService(
        AppConfig appConfig,
        IStreamInfoUpdater streamInfoUpdater,
        ILogger<StreamMonitorService> logger)
    {
        _appConfig = appConfig;
        _streamInfoUpdater = streamInfoUpdater;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Don't block startup
        await Task.Yield();

        _logger.LogInformation("{ServiceName} service started", nameof(StreamMonitorService));

        try
        {
            var userLogin = _appConfig.TwitchOptions.UserLogin;

            while (!stoppingToken.IsCancellationRequested)
            {
                // var streamInfo = await _twitchApi.GetStreamInfoAsync(userLogin, stoppingToken);
                var streamInfo = await _streamInfoUpdater.RefreshAsync(stoppingToken);

                if (streamInfo is not null)
                {
                    _logger.LogInformation("User {UserLogin} is live: {StreamInfo}",
                                           userLogin, streamInfo);
                }
                else
                {
                    _logger.LogInformation("User {UserLogin} is offline", userLogin);
                }

                await DelayNextCheck(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("{ServiceName} stopping...", nameof(StreamMonitorService));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Unexpected stream monitor error");
            throw;
        }
    }

    private Task DelayNextCheck(CancellationToken stoppingToken)
    {
        var pollInterval = _appConfig.RecorderOptions.PollInterval;
        _logger.LogDebug("Next stream info check: {CheckTime:T}", DateTime.Now.Add(pollInterval));

        return Task.Delay(pollInterval, stoppingToken);
    }
}