namespace StreamKeepr.Infrastructure.Media;

/// <summary>How one recorder tool process stopped.</summary>
public enum ProcessStop
{
    /// <summary>The process had already exited before a stop was requested.</summary>
    AlreadyStopped,

    /// <summary>The process exited after the graceful signal.</summary>
    Graceful,

    /// <summary>The process exited after the force kill and the PID identity was verified.</summary>
    Forced,

    /// <summary>The force wait elapsed while the process task was still pending.</summary>
    TimedOut,

    /// <summary>The force kill finished, but the PID identity could not prove the process is gone.</summary>
    Unverified,
}

/// <summary>
/// A recorder tool that could not be proven to have stopped (glossary: Unverified shutdown). The
/// previous writer may still be writing, so the channel must never start another one. The text
/// holds only lifecycle facts; it never contains a command line or token.
/// </summary>
/// <param name="Tool">The tool that did not stop: <c>streamlink</c> or <c>ffmpeg</c>.</param>
/// <param name="ProcessId">The direct process ID captured at start, when it was readable.</param>
/// <param name="CapturedStartTime">The start time captured at launch, when it was readable.</param>
/// <param name="Stop">How the bounded stop ended: <see cref="ProcessStop.TimedOut"/> or <see cref="ProcessStop.Unverified"/>.</param>
/// <param name="TaskPending">True when the process task had not completed after the force wait.</param>
/// <param name="ObservedState">What the process table reported about the captured PID.</param>
public sealed record UnverifiedShutdown(
    string Tool,
    int? ProcessId,
    DateTime? CapturedStartTime,
    ProcessStop Stop,
    bool TaskPending,
    string ObservedState)
{
    public override string ToString() =>
        $"{Tool} shutdown could not be verified: "
        + $"pid {ProcessId?.ToString() ?? "unknown"}, "
        + $"started {CapturedStartTime?.ToString("O") ?? "unknown"}, "
        + $"stop {Stop}, "
        + $"task {(TaskPending ? "pending" : "completed")}; "
        + $"observed {ObservedState}";
}
