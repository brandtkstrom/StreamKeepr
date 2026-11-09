using System.Collections.Immutable;
using System.Threading.Channels;
using StreamRecorder.Config;
using StreamRecorder.Domain;
using StreamRecorder.Storage;

namespace StreamRecorder.Recording;

public class StreamRecorderService : BackgroundService
{
    private readonly AppConfig _appConfig;
    private readonly IStreamInfoAccessor _streamInfo;
    private readonly IStreamInfoUpdater _streamInfoUpdater;
    private readonly IRecordingStore _recordingStore;
    private readonly IStreamCapturePipeline _streamCapturePipeline;
    private readonly ILogger<StreamRecorderService> _logger;

    private readonly Channel<RecorderEvent> _eventChannel = Channel.CreateUnbounded<RecorderEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true
    });

    public StreamRecorderService(AppConfig appConfig,
                                 IStreamInfoAccessor streamInfo,
                                 IStreamInfoUpdater streamInfoUpdater,
                                 IRecordingStore recordingStore,
                                 IStreamCapturePipeline streamCapturePipeline,
                                 ILogger<StreamRecorderService> logger)
    {
        _appConfig = appConfig;
        _streamInfo = streamInfo;
        _streamInfoUpdater = streamInfoUpdater;
        _recordingStore = recordingStore;
        _streamCapturePipeline = streamCapturePipeline;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Don't block startup
        await Task.Yield();

        _logger.LogInformation("{ServiceName} service started", nameof(StreamRecorderService));

        try
        {
            RecorderState state = new RecorderState.Idle();
            await _eventChannel.Writer.WriteAsync(new RecorderEvent.Initialized(), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var evt = await _eventChannel.Reader.ReadAsync(stoppingToken);

                var transitionResult = RecorderStateMachine.Transition(state, evt);

                if (transitionResult.NextState is RecorderState.Stopped stopped)
                {
                    if (!string.IsNullOrWhiteSpace(stopped.Error))
                        _logger.LogError(stopped.Error);

                    break;
                }

                state = transitionResult.NextState;

                await ExecuteCommandsAsync(transitionResult.Commands, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("{ServiceName} stopping...", nameof(StreamRecorderService));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected stream monitor error");
            _eventChannel.Writer.TryComplete(ex);
            throw;
        }
        finally
        {
            _eventChannel.Writer.TryComplete();
        }
    }

    private async Task ExecuteCommandsAsync(IReadOnlyList<RecorderCommand> commands, CancellationToken stoppingToken)
    {
        foreach (var command in commands)
        {
            stoppingToken.ThrowIfCancellationRequested();

            switch (command)
            {
                case RecorderCommand.PollStreamInfo:
                    RecorderEvent polledEvent = _streamInfo.Get() is { } info
                                                    ? new RecorderEvent.StreamOnline(info)
                                                    : new RecorderEvent.StreamInfoPolled();

                    await _eventChannel.Writer.WriteAsync(polledEvent, stoppingToken);
                    break;
                case RecorderCommand.Log logCmd:
                    _logger.Log(logCmd.Level, "[{ServiceName}] {Message}", nameof(StreamRecorderService), logCmd.Message);
                    break;
                case RecorderCommand.StartCapture captureCmd:
                    if (captureCmd.Attempt > 1)
                    {
                        var streamInfo = await _streamInfoUpdater.RefreshAsync(stoppingToken);
                        if (streamInfo is null)
                        {
                            await _eventChannel.Writer.WriteAsync(new RecorderEvent.CaptureEnded(), stoppingToken);
                            break;
                        }

                        captureCmd = captureCmd with { StreamInfo = streamInfo };
                    }
                    // create recording target and verify writable
                    var recordingTarget = _recordingStore.CreateTarget(captureCmd.StreamInfo);

                    if (!_recordingStore.Writable())
                    {
                        var writeError = new RecorderEvent.Faulted($"cannot write to output path: {recordingTarget.Directory}");
                        await _eventChannel.Writer.WriteAsync(writeError, stoppingToken);
                        break;
                    }

                    // init recording result and start stream capture
                    RecordingResult recordingResult = new RecordingResult(RecordingOutcome.StreamEnded, recordingTarget.Directory);

                    try
                    {
                        recordingResult = await _streamCapturePipeline.StartCaptureAsync(recordingTarget, stoppingToken);
                    }
                    catch (Exception)
                    {
                        recordingResult = recordingResult with {Outcome = RecordingOutcome.Failed};
                    }
                    finally
                    {
                        RecorderEvent captureEvent = recordingResult.Outcome is not RecordingOutcome.Failed
                                                         ? new RecorderEvent.CaptureEnded()
                                                         : new RecorderEvent.CaptureError(captureCmd.StreamInfo, captureCmd.Attempt);

                        await _eventChannel.Writer.WriteAsync(captureEvent, stoppingToken);
                    }

                    break;
                case RecorderCommand.Sleep:
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    break;
                case RecorderCommand.StartCooldown:
                    await Task.Delay(_appConfig.RecorderOptions.Cooldown, stoppingToken);
                    await _eventChannel.Writer.WriteAsync(new RecorderEvent.CooldownEnded(), stoppingToken);
                    break;
            }
        }
    }
}