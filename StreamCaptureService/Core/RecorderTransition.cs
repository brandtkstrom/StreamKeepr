using LanguageExt;
using static LanguageExt.Prelude;

namespace StreamRecorder.Core;

/// <summary>Pure state machine: the single place recorder behavior lives.</summary>
public static class RecorderTransition
{
    /// <summary>
    /// How many consecutive pipeline failures are tolerated before the recorder stops
    /// retrying and parks in <see cref="RecorderState.Faulted"/> until the stream ends.
    /// </summary>
    public const int MaxConsecutiveFailures = 3;

    /// <summary>
    /// Pure function. Given identical inputs, always returns identical output.
    /// No clocks, no I/O, no randomness.
    /// </summary>
    public static (RecorderState Next, Option<RecorderCommand> Cmd) Transition(
        RecorderState state,
        RecorderEvent evt,
        DateTimeOffset sessionStart,
        Func<DateTimeOffset, string> buildSessionPath)
    {
        return (state, evt) switch
        {
            // Live detected with no session ─ start a new one
            (RecorderState.Offline, RecorderEvent.StreamWentLive) =>
                BeginStarting(0, sessionStart, buildSessionPath),

            // Pipeline confirmed running
            (RecorderState.Starting s, RecorderEvent.ProcessStarted) =>
                (new RecorderState.Recording(s.SessionPath, s.ConsecutiveFailures), None),

            // Stream ended mid-recording ─ stop pipeline
            (RecorderState.Recording s, RecorderEvent.StreamWentOffline) =>
                (new RecorderState.Stopping(s.ConsecutiveFailures),
                 Some<RecorderCommand>(new RecorderCommand.StopPipeline())),

            // Explicit stop request
            (RecorderState.Recording s, RecorderEvent.StopRequested) =>
                (new RecorderState.Stopping(s.ConsecutiveFailures),
                 Some<RecorderCommand>(new RecorderCommand.StopPipeline())),

            // Pipeline exited while stopping ─ session is over
            (RecorderState.Stopping, RecorderEvent.ProcessExited) =>
                (new RecorderState.Offline(), None),

            // A fault while stopping ends the session rather than banking a retry: the stream
            // is already offline or the host is shutting down, so there is nothing to resume.
            (RecorderState.Stopping, RecorderEvent.PipelineFailed) =>
                (new RecorderState.Offline(), None),

            // Pipeline exited cleanly while the stream stayed live ─ keep the session resumable
            (RecorderState.Recording s, RecorderEvent.ProcessExited) =>
                (new RecorderState.Interrupted(s.SessionPath, s.ConsecutiveFailures), None),

            // Failure from anywhere → Faulted, banking the failure against the budget.
            (_, RecorderEvent.PipelineFailed(var reason)) =>
                (new RecorderState.Faulted(SessionPathOf(state), reason, ConsecutiveFailuresOf(state) + 1), None),

            // Retry while the failure budget allows ─ resume the same session.
            (RecorderState.Faulted {ConsecutiveFailures: < MaxConsecutiveFailures} f, RecorderEvent.StreamWentLive) =>
                ResumeStarting(f.SessionPath, f.ConsecutiveFailures, sessionStart, buildSessionPath),

            // Budget exhausted — stay parked until the stream ends.
            (RecorderState.Faulted, RecorderEvent.StreamWentLive) =>
                (state, None),

            // Clean-exit recovery while the stream is still live ─ resume the same session.
            (RecorderState.Interrupted i, RecorderEvent.StreamWentLive) =>
                ResumeStarting(i.SessionPath, i.ConsecutiveFailures, sessionStart, buildSessionPath),

            // The stream ended ─ close the session and reset the budget.
            (RecorderState.Faulted, RecorderEvent.StreamWentOffline) =>
                (new RecorderState.Offline(), None),

            (RecorderState.Interrupted, RecorderEvent.StreamWentOffline) =>
                (new RecorderState.Offline(), None),

            // Shutdown while no pipeline is running.
            (RecorderState.Interrupted, RecorderEvent.StopRequested) =>
                (new RecorderState.Offline(), None),

            // No valid transition — hold state, no command
            _ => (state, None)
        };
    }

    /// <summary>Failures banked so far, or 0 for states that cannot hold a budget.</summary>
    private static int ConsecutiveFailuresOf(RecorderState state) => state switch
    {
        RecorderState.Starting s => s.ConsecutiveFailures,
        RecorderState.Recording s => s.ConsecutiveFailures,
        RecorderState.Stopping s => s.ConsecutiveFailures,
        RecorderState.Interrupted s => s.ConsecutiveFailures,
        RecorderState.Faulted s => s.ConsecutiveFailures,
        _ => 0
    };

    /// <summary>The session a state belongs to, or null when it has none.</summary>
    private static string? SessionPathOf(RecorderState state) => state switch
    {
        RecorderState.Starting s => s.SessionPath,
        RecorderState.Recording s => s.SessionPath,
        RecorderState.Interrupted s => s.SessionPath,
        RecorderState.Faulted s => s.SessionPath,
        _ => null
    };

    /// <summary>Starts a fresh session at the supplied timestamp.</summary>
    private static (RecorderState, Option<RecorderCommand>) BeginStarting(
        int consecutiveFailures,
        DateTimeOffset sessionStart,
        Func<DateTimeOffset, string> buildSessionPath)
    {
        var path = buildSessionPath(sessionStart);

        return (new RecorderState.Starting(path, consecutiveFailures),
                Some<RecorderCommand>(new RecorderCommand.StartPipeline(path)));
    }

    /// <summary>Resumes an existing session, or starts a fresh one when the fault had none.</summary>
    private static (RecorderState, Option<RecorderCommand>) ResumeStarting(
        string? sessionPath,
        int consecutiveFailures,
        DateTimeOffset sessionStart,
        Func<DateTimeOffset, string> buildSessionPath)
    {
        if (string.IsNullOrEmpty(sessionPath))
            return BeginStarting(consecutiveFailures, sessionStart, buildSessionPath);

        return (new RecorderState.Starting(sessionPath, consecutiveFailures),
                Some<RecorderCommand>(new RecorderCommand.StartPipeline(sessionPath)));
    }
}