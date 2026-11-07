
using StreamRecorder.Config;
using StreamRecorder.Domain;

namespace StreamRecorder.Recording;

public class StreamRecorderService : BackgroundService
{
    private readonly AppConfig _appConfig;
    private readonly StreamInfoAccessor _streamInfo;
    private readonly ILogger<StreamRecorderService> _logger;

    public StreamRecorderService(AppConfig appConfig, StreamInfoAccessor streamInfo, ILogger<StreamRecorderService> logger)
    {
        _appConfig = appConfig;
        _streamInfo = streamInfo;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Don't block startup
        await Task.Yield();

        _logger.LogInformation("{ServiceName} service started", nameof(StreamRecorderService));

        try { }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("{ServiceName} stopping...", nameof(StreamRecorderService));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Unexpected stream monitor error");
            throw;
        }
    }
}