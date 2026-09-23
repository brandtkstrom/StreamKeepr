using StreamRecorder.Twitch;

namespace StreamRecorder;

public class Worker : BackgroundService
{
    private readonly ITwitchApiClient _twitchApi;
    private readonly ILogger<Worker> _logger;

    public Worker(ITwitchApiClient twitchApi, ILogger<Worker> logger)
    {
        _twitchApi = twitchApi;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // WIP - test api call & log response dto
            var info = await _twitchApi.GetStreamInfoAsync("alveussanctuary", stoppingToken);
            if (info is not null)
            {
                _logger.LogInformation("StreamInfo: {@Info}", info);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "An error occured");
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);

            await Task.Delay(1000, stoppingToken);
        }
    }
}