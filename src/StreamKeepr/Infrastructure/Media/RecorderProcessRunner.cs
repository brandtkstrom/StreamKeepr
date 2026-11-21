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

/// <summary>
/// One tool process for a recorder job. <paramref name="Tool"/> names it in diagnostics
/// (<c>streamlink</c> or <c>ffmpeg</c>). Standard output goes either to
/// <paramref name="StandardOutput"/> as bytes or to a line callback, never both.
/// </summary>
internal sealed record ToolLaunch(
    string Tool,
    string FilePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    Stream? StandardInput = null,
    Stream? StandardOutput = null);

/// <summary>
/// Starts one tool process and reports its output lines. Production runs real processes through
/// CliWrap; tests substitute a fake that scripts output lines and completion, so progress,
/// ad-break, stall, and shutdown scenarios run without child processes.
/// </summary>
internal interface IRecorderProcessRunner
{
    /// <summary>
    /// Starts <paramref name="launch"/>. <paramref name="onStdoutLine"/> is required exactly when
    /// <see cref="ToolLaunch.StandardOutput"/> is null. Callbacks may run concurrently with each
    /// other and with the caller. <paramref name="gracefulStop"/> asks the process to exit;
    /// <paramref name="forceKill"/> kills it. The returned completion never faults.
    /// </summary>
    RunningProcess Start(
        ToolLaunch launch,
        Func<string, ValueTask>? onStdoutLine,
        Action<string> onStderrLine,
        CancellationToken forceKill,
        CancellationToken gracefulStop);
}

/// <summary>
/// Starts a tool through CliWrap and captures the direct PID and its start time before waiting.
/// The CliWrap task completes only after the direct process exits and both streams reach EOF; a
/// descendant can therefore hold the task open after the direct PID is gone.
/// </summary>
internal sealed class CliWrapProcessRunner : IRecorderProcessRunner
{
    public RunningProcess Start(
        ToolLaunch launch,
        Func<string, ValueTask>? onStdoutLine,
        Action<string> onStderrLine,
        CancellationToken forceKill,
        CancellationToken gracefulStop)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(onStderrLine);
        if ((launch.StandardOutput is null) == (onStdoutLine is null))
        {
            throw new ArgumentException(
                "Standard output needs exactly one of a stream or a line callback.",
                nameof(onStdoutLine));
        }

        Command command = Cli.Wrap(launch.FilePath)
            .WithWorkingDirectory(launch.WorkingDirectory)
            .WithArguments(launch.Arguments)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(launch.StandardOutput is { } output
                ? PipeTarget.ToStream(output)
                : PipeTarget.ToDelegate(line => onStdoutLine!(line).AsTask()))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(onStderrLine));
        if (launch.StandardInput is { } input)
            command = command.WithStandardInputPipe(PipeSource.FromStream(input));

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
