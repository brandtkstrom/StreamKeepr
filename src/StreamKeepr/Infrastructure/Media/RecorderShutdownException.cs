using StreamKeepr.Domain;

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

    /// <summary>The force wait elapsed while the CliWrap task was still pending.</summary>
    TimedOut,

    /// <summary>The force kill finished, but the PID identity could not prove the process is gone.</summary>
    Unverified,
}

/// <summary>
/// Raised when a recorder tool could not be proven to have stopped. The job must not report a
/// normal exit: the previous writer may still be writing, so the coordinator must never start
/// another one. The message deliberately contains only lifecycle facts (tool, PID, start time,
/// stop result, task state, observed process state); it never contains a command line or token.
/// </summary>
public sealed class RecorderShutdownException : Exception
{
    public RecorderShutdownException(
        JobId job,
        string tool,
        int? processId,
        DateTime? capturedStartTime,
        ProcessStop stop,
        bool taskPending,
        string observedState)
        : base(BuildMessage(tool, processId, capturedStartTime, stop, taskPending, observedState))
    {
        Job = job;
        Tool = tool;
        ProcessId = processId;
        CapturedStartTime = capturedStartTime;
        Stop = stop;
        TaskPending = taskPending;
        ObservedState = observedState;
    }

    /// <summary>The part whose writer may still be running.</summary>
    public JobId Job { get; }

    /// <summary>The tool that did not stop: <c>streamlink</c> or <c>ffmpeg</c>.</summary>
    public string Tool { get; }

    /// <summary>The direct process ID captured when the tool started, when it was readable.</summary>
    public int? ProcessId { get; }

    /// <summary>The tool's start time captured immediately after launch, when it was readable.</summary>
    public DateTime? CapturedStartTime { get; }

    /// <summary>How the bounded stop ended.</summary>
    public ProcessStop Stop { get; }

    /// <summary>True when the CliWrap task had not completed after the force wait.</summary>
    public bool TaskPending { get; }

    /// <summary>What the process table reported about the captured PID.</summary>
    public string ObservedState { get; }

    private static string BuildMessage(
        string tool,
        int? processId,
        DateTime? capturedStartTime,
        ProcessStop stop,
        bool taskPending,
        string observedState) =>
        $"{tool} shutdown could not be verified: "
        + $"pid {processId?.ToString() ?? "unknown"}, "
        + $"started {capturedStartTime?.ToString("O") ?? "unknown"}, "
        + $"stop {stop}, "
        + $"task {(taskPending ? "pending" : "completed")}; "
        + $"observed {observedState}";
}
