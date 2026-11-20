using CliWrap;

namespace StreamKeepr.Infrastructure.Media;

/// <summary>
/// One tool process started by a recorder job: its captured identity and the task that completes
/// when CliWrap has drained both streams and the direct process has exited.
/// </summary>
internal sealed record RunningProcess(
    int? ProcessId,
    DateTime? StartTime,
    Task<ProcessOutcome> Completion);

/// <summary>A completed tool run, including the identity captured when it started.</summary>
internal readonly record struct ProcessOutcome(
    int? ExitCode,
    Exception? Error,
    bool Cancelled,
    int? ProcessId,
    DateTime? StartTime,
    ProcessStop Stop,
    bool TaskPending);

/// <summary>Starts a tool command. Tests substitute a fake to drive shutdown deterministically.</summary>
internal interface IRecorderProcessRunner
{
    RunningProcess Start(
        Command command,
        CancellationToken forceKill,
        CancellationToken gracefulStop);
}

/// <summary>
/// Starts a CliWrap command and captures the direct PID and its start time before waiting. The
/// CliWrap task completes only after the direct process exits and both streams reach EOF; a
/// descendant can therefore hold the task open after the direct PID is gone.
/// </summary>
internal sealed class CliWrapProcessRunner : IRecorderProcessRunner
{
    public RunningProcess Start(
        Command command,
        CancellationToken forceKill,
        CancellationToken gracefulStop)
    {
        CommandTask<CommandResult> task;
        try
        {
            task = command.ExecuteAsync(forceKill, gracefulStop);
        }
        catch (Exception ex)
        {
            // The process never started; there is no writer to verify.
            return new RunningProcess(
                ProcessId: null,
                StartTime: null,
                Task.FromResult(new ProcessOutcome(
                    ExitCode: null,
                    Error: ex,
                    Cancelled: false,
                    ProcessId: null,
                    StartTime: null,
                    ProcessStop.AlreadyStopped,
                    TaskPending: false)));
        }

        int? processId = null;
        DateTime? startTime = null;
        try
        {
            processId = task.ProcessId;
            startTime = ProcessIdentityInspector.ReadStartTime(processId.Value);
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException
            || ProcessIdentityInspector.IsUnreadable(ex))
        {
            // The direct process may already have exited, or its identity may be unreadable.
            // Leave the start time unknown; the stop path fails closed when it cannot verify.
        }

        return new RunningProcess(processId, startTime, AwaitExitAsync(task, processId, startTime));
    }

    private static async Task<ProcessOutcome> AwaitExitAsync(
        CommandTask<CommandResult> task,
        int? processId,
        DateTime? startTime)
    {
        try
        {
            CommandResult result = await task.Task.ConfigureAwait(false);
            return Completed(result.ExitCode, processId, startTime, cancelled: false, error: null);
        }
        catch (OperationCanceledException)
        {
            return Completed(exitCode: null, processId, startTime, cancelled: true, error: null);
        }
        catch (Exception ex)
        {
            return Completed(exitCode: null, processId, startTime, cancelled: false, error: ex);
        }
    }

    private static ProcessOutcome Completed(
        int? exitCode,
        int? processId,
        DateTime? startTime,
        bool cancelled,
        Exception? error) =>
        new(
            exitCode,
            error,
            cancelled,
            processId,
            startTime,
            ProcessStop.AlreadyStopped,
            TaskPending: false);
}
