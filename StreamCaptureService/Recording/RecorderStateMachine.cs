using System.Collections.Immutable;
using StreamRecorder.Domain;

namespace StreamRecorder.Recording;

public static class RecorderStateMachine
{
    // A pure function: (State, Event) -> State
    public static TransitionResult Transition(RecorderState state, RecorderEvent evt) =>
        (state, evt) switch
        {
            (RecorderState.Idle, RecorderEvent.Initialized) =>
                TransitionResult.Create(new RecorderState.Polling(),
                                        new RecorderCommand.Log(LogLevel.Information, "Stream recorder initialized"),
                                        new RecorderCommand.PollStreamInfo()),
            (RecorderState.Polling, RecorderEvent.StreamInfoPolled) =>
                TransitionResult.Create(state,
                                        new RecorderCommand.Log(LogLevel.Debug, "Channel polled: stream offline"),
                                        new RecorderCommand.Sleep(TimeSpan.FromSeconds(10)),
                                        new RecorderCommand.PollStreamInfo()),
            (RecorderState.Polling, RecorderEvent.StreamOnline o) =>
                TransitionResult.Create(new RecorderState.Recording(),
                                        new RecorderCommand.Log(LogLevel.Information, "Stream is live: starting capture"),
                                        new RecorderCommand.StartCapture(o.StreamInfo)),
            (RecorderState.Recording, RecorderEvent.CaptureError {AttemptCount: < 5} err) =>
                TransitionResult.Create(new RecorderState.Recording(),
                                        new RecorderCommand.Log(LogLevel.Information, $"Stream capture error: retry capture attempt {err.AttemptCount + 1}"),
                                        new RecorderCommand.Sleep(TimeSpan.FromSeconds(2)),
                                        new RecorderCommand.StartCapture(err.StreamInfo, err.AttemptCount + 1)),
            (RecorderState.Recording, RecorderEvent.CaptureError {AttemptCount: >= 5} err) =>
                TransitionResult.Create(new RecorderState.Cooldown(),
                                        new RecorderCommand.Log(LogLevel.Information, $"Stream capture failed after {err.AttemptCount} attempts: starting cooldown"),
                                        new RecorderCommand.StartCooldown()),
            (RecorderState.Recording, RecorderEvent.CaptureEnded) =>
                TransitionResult.Create(new RecorderState.Cooldown(),
                                        new RecorderCommand.Log(LogLevel.Information, "Stream capture ended: starting cooldown"),
                                        new RecorderCommand.StartCooldown()),
            (RecorderState.Cooldown, RecorderEvent.CooldownEnded) =>
                TransitionResult.Create(new RecorderState.Polling(),
                                        new RecorderCommand.Log(LogLevel.Information, "Cooldown complete: restarting polling loop"),
                                        new RecorderCommand.PollStreamInfo()),
            (_, RecorderEvent.Faulted fault) =>
                TransitionResult.Create(new RecorderState.Stopped(fault.Message)),
            // Discard invalid/ignored transitions
            var _ => TransitionResult.Create(state)
        };
}

public record TransitionResult(
    RecorderState NextState,
    IReadOnlyList<RecorderCommand> Commands)
{
    // Syntactic sugar helper to make the state machine code incredibly clean
    public static TransitionResult Create(RecorderState state, params RecorderCommand[] commands)
        => new(state, commands.ToImmutableList());
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
    public sealed record CaptureEnded : RecorderEvent;
    public sealed record CaptureError(StreamInfo StreamInfo, int AttemptCount) : RecorderEvent;
    public sealed record Faulted(string Message) : RecorderEvent;
    public sealed record CooldownEnded : RecorderEvent;
}

public abstract record RecorderCommand
{
    private RecorderCommand() { }

    public sealed record PollStreamInfo : RecorderCommand;
    public sealed record StartCapture(StreamInfo StreamInfo, int Attempt = 1) : RecorderCommand;
    public sealed record StartCooldown : RecorderCommand;
    public sealed record Sleep(TimeSpan SleepDuration) : RecorderCommand;
    public sealed record Log(LogLevel Level, string Message) : RecorderCommand;
}