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
    private readonly IRecordingStore _recordingStore;
    private readonly IStreamCapturePipeline _streamCapturePipeline;
    private readonly ILogger<StreamRecorderService> _logger;

    private readonly Channel<RecorderEvent> _eventChannel = Channel.CreateUnbounded<RecorderEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true
    });

    public StreamRecorderService(AppConfig appConfig, IStreamInfoAccessor streamInfo, IRecordingStore recordingStore, IStreamCapturePipeline streamCapturePipeline, ILogger<StreamRecorderService> logger)
    {
        _appConfig = appConfig;
        _streamInfo = streamInfo;
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
                    // create recording target and verify writable
                    var recordingTarget = _recordingStore.CreateTarget(captureCmd.StreamInfo);
                    if (!_recordingStore.Writable())
                    {
                        var writeError = new RecorderEvent.Faulted($"cannot write to output path: {recordingTarget.Directory}");
                        await _eventChannel.Writer.WriteAsync(writeError, stoppingToken);
                        break;
                    }

                    // init recording result and start stream capture
                    RecordingResult recordingResult = new RecordingResult(RecordingOutcome.None, recordingTarget.Directory);
                    try
                    {
                        // TODO - fix recording outcome/result handling
                        var captureResult = await _streamCapturePipeline.StartCaptureAsync(recordingTarget, stoppingToken);
                        recordingResult = recordingResult with {Outcome = RecordingOutcome.StreamEnded};
                    }
                    catch (Exception)
                    {
                        recordingResult = recordingResult with {Outcome = RecordingOutcome.Failed};
                    }
                    finally
                    {
                        await _eventChannel.Writer.WriteAsync(new RecorderEvent.CaptureEnded(recordingResult), stoppingToken);
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

public abstract record RecorderState
{
    private RecorderState() { }

    public sealed record Idle : RecorderState;
    public sealed record Polling : RecorderState;
    public sealed record Recording : RecorderState;
    public sealed record Cooldown : RecorderState;
    public sealed record Stopped(string? Error) : RecorderState;
}

public abstract record RecorderEvent
{
    private RecorderEvent() { }

    public sealed record Initialized : RecorderEvent;
    public sealed record StreamInfoPolled : RecorderEvent;
    public sealed record StreamOnline(StreamInfo StreamInfo) : RecorderEvent;
    public sealed record CaptureEnded(RecordingResult Result) : RecorderEvent;
    public sealed record Faulted(string Message) : RecorderEvent;
    public sealed record CooldownEnded : RecorderEvent;
}

public abstract record RecorderCommand
{
    private RecorderCommand() { }

    public sealed record PollStreamInfo : RecorderCommand;
    public sealed record StartCapture(StreamInfo StreamInfo) : RecorderCommand;
    public sealed record StartCooldown : RecorderCommand;
    public sealed record Sleep : RecorderCommand;

    public sealed record Log(LogLevel Level, string Message) : RecorderCommand;
    // Restart?
}

public record TransitionResult(
    RecorderState NextState,
    IReadOnlyList<RecorderCommand> Commands)
{
    // Syntactic sugar helper to make the state machine code incredibly clean
    public static TransitionResult Create(RecorderState state, params RecorderCommand[] commands)
        => new(state, commands.ToImmutableList());
}

public static class RecorderStateMachine
{
    // A pure function: (State, Event) -> State
    public static TransitionResult Transition(RecorderState state, RecorderEvent evt) =>
        (state, evt) switch
        {
            (RecorderState.Idle, RecorderEvent.Initialized) =>
                TransitionResult.Create(new RecorderState.Polling(),
                                        new RecorderCommand.Log(LogLevel.Information, "stream recorder initialized"),
                                        new RecorderCommand.PollStreamInfo()),
            (RecorderState.Polling, RecorderEvent.StreamInfoPolled) =>
                TransitionResult.Create(state,
                                        new RecorderCommand.Log(LogLevel.Debug, "channel polled - stream offline"),
                                        new RecorderCommand.Sleep(),
                                        new RecorderCommand.PollStreamInfo()),
            (RecorderState.Polling, RecorderEvent.StreamOnline o) =>
                TransitionResult.Create(new RecorderState.Recording(),
                                        new RecorderCommand.Log(LogLevel.Information, "stream is live: starting capture"),
                                        new RecorderCommand.StartCapture(o.StreamInfo)),
            (RecorderState.Recording, RecorderEvent.CaptureEnded) =>
                TransitionResult.Create(new RecorderState.Cooldown(),
                                        new RecorderCommand.Log(LogLevel.Information, "stream capture ended: cooldown started"),
                                        new RecorderCommand.StartCooldown()),
            (RecorderState.Cooldown, RecorderEvent.CooldownEnded) =>
                TransitionResult.Create(new RecorderState.Polling(),
                                        new RecorderCommand.Log(LogLevel.Information, "cooldown complete: restarting polling loop"),
                                        new RecorderCommand.PollStreamInfo()),
            (_, RecorderEvent.Faulted fault) =>
                TransitionResult.Create(new RecorderState.Stopped(fault.Message)),
            // Discard invalid/ignored transitions
            var _ => TransitionResult.Create(state)
        };
}