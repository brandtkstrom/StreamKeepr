using System.IO.Pipelines;
using CliWrap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;

namespace StreamKeepr.Infrastructure.Media;

/// <summary>
/// Runs Streamlink and FFmpeg for exactly one part of one session. The job publishes
/// <see cref="DvrEvent.PartProducingMedia"/> at most once and publishes exactly one
/// <see cref="DvrEvent.RecorderExited"/> when the job completes.
/// </summary>
public sealed class RecorderJob(
    IOptions<RecorderOptions> options,
    TimeProvider clock,
    ILogger<RecorderJob> logger)
{
    private const int StderrCapacity = 20;

    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ForceStopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Records the job's part into <see cref="Session.Directory"/>. A part that already has any
    /// file is never overwritten; the job reports an exit without starting either process.
    /// A requested <paramref name="stopRequested"/> runs the bounded shutdown and reports
    /// <see cref="RecorderExitKind.Stopped"/> unless a force path was needed.
    /// </summary>
    public async Task RunAsync(
        Session session,
        JobId job,
        Func<DvrEvent, CancellationToken, ValueTask> publish,
        CancellationToken stopRequested = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(publish);

        RecorderOptions recorder = options.Value;
        Directory.CreateDirectory(session.Directory);

        if (PartFiles.Exists(session.Directory, job.Part))
        {
            await publish(
                new DvrEvent.RecorderExited(
                    job,
                    clock.GetUtcNow(),
                    RecorderExitKind.Exited,
                    $"part {job.Part:D3} already exists; refusing to overwrite"),
                CancellationToken.None);
            return;
        }

        if (stopRequested.IsCancellationRequested)
        {
            await publish(
                new DvrEvent.RecorderExited(
                    job,
                    clock.GetUtcNow(),
                    RecorderExitKind.Stopped,
                    "stop requested before start"),
                CancellationToken.None);
            return;
        }

        var tail = new StderrTail(StderrCapacity);
        var parser = new FfmpegProgressParser();
        int producing = 0;
        long lastMediaAdvanceTicks = clock.GetUtcNow().UtcTicks;
        long adGraceUntilTicks = 0;

        string Redact(string line) =>
            string.IsNullOrEmpty(recorder.TwitchUserOAuthToken)
                ? line
                : line.Replace(recorder.TwitchUserOAuthToken, "***", StringComparison.Ordinal);

        // The bounded pipe is the backpressure boundary between the two tools: Streamlink
        // pauses at 4 MiB and resumes when FFmpeg has consumed back down to 2 MiB.
        Pipe pipe = RecorderPipe.Create();

        using var ffmpegGraceful = new CancellationTokenSource();
        using var ffmpegForce = new CancellationTokenSource();
        using var streamlinkGraceful = new CancellationTokenSource();
        using var streamlinkForce = new CancellationTokenSource();

        Command ffmpegCommand = Cli.Wrap(recorder.FfmpegPath)
            .WithWorkingDirectory(session.Directory)
            .WithArguments(FfmpegArgs.ForPart(session.Directory, job.Part, recorder.SegmentSeconds))
            .WithValidation(CommandResultValidation.None)
            .WithStandardInputPipe(PipeSource.FromStream(pipe.Reader.AsStream()))
            .WithStandardOutputPipe(PipeTarget.ToDelegate(async line =>
            {
                if (parser.Feed(line) is not { } sample)
                    return;

                // total_size is N/A for the HLS muxer. Byte progress comes from the part files.
                long bytes = PartFiles.TotalBytes(session.Directory, job.Part);
                logger.LogDebug(
                    "Recorder {Job} progress: {Bytes} bytes, media {MediaTime}",
                    job,
                    bytes,
                    sample.MediaTime);

                if (!sample.Advanced)
                    return;

                DateTimeOffset advancedAt = clock.GetUtcNow();
                Interlocked.Exchange(ref lastMediaAdvanceTicks, advancedAt.UtcTicks);
                if (Interlocked.Exchange(ref producing, 1) == 0)
                {
                    await publish(
                        new DvrEvent.PartProducingMedia(job, advancedAt),
                        CancellationToken.None);
                }
            }))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
            {
                string safe = Redact(line);
                tail.Add("ffmpeg", safe);
                logger.LogDebug("[ffmpeg {Job}] {Line}", job, safe);
            }));

        Command streamlinkCommand = Cli.Wrap(recorder.StreamlinkPath)
            .WithWorkingDirectory(session.Directory)
            .WithArguments(StreamlinkArgs.For(session.Channel, recorder))
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStream(pipe.Writer.AsStream()))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
            {
                string safe = Redact(line);

                // Streamlink announces skipped ads before it starves FFmpeg of media. Extend the
                // grace window before logging so a later stall check observes the new deadline.
                if (StreamlinkAdBreak.TryGetGraceUntil(line, clock.GetUtcNow(), out DateTimeOffset graceUntil))
                {
                    long candidate = graceUntil.UtcTicks;
                    long current = Interlocked.Read(ref adGraceUntilTicks);
                    while (candidate > current)
                    {
                        long seen = Interlocked.CompareExchange(
                            ref adGraceUntilTicks, candidate, current);
                        if (seen == current)
                            break;
                        current = seen;
                    }
                }

                tail.Add("streamlink", safe);
                logger.LogDebug("[streamlink {Job}] {Line}", job, safe);
            }));

        Task<ProcessOutcome> ffmpegTask = RunProcessAsync(
            ffmpegCommand, ffmpegForce.Token, ffmpegGraceful.Token);
        Task<ProcessOutcome> streamlinkTask = RunProcessAsync(
            streamlinkCommand, streamlinkForce.Token, streamlinkGraceful.Token);

        var stopSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration stopRegistration = stopRequested.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            stopSignal);

        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watchdog = new CancellationTokenSource();
        Task watchdogTask = WatchForStallAsync(
            () => Math.Max(
                Interlocked.Read(ref lastMediaAdvanceTicks),
                Interlocked.Read(ref adGraceUntilTicks)),
            TimeSpan.FromSeconds(recorder.StallTimeoutSeconds),
            stalled,
            watchdog.Token);

        string reason;
        bool forcedKill = false;
        bool finalizeTimedOut = false;
        bool stalledEnd = false;
        try
        {
            Task first = await Task.WhenAny(
                streamlinkTask, ffmpegTask, stopSignal.Task, stalled.Task);

            stalledEnd = first == stalled.Task;
            reason = first == streamlinkTask ? "streamlink exited"
                : first == ffmpegTask ? "ffmpeg exited first"
                : stalledEnd ? $"stall: no new media for {recorder.StallTimeoutSeconds} s"
                : "stop requested";

            // Contract order: release the reader before stopping Streamlink when FFmpeg is
            // already gone, so Streamlink cannot block forever on a full pipe.
            bool readerCompleted = false;
            if (ffmpegTask.IsCompleted)
            {
                await pipe.Reader.CompleteAsync();
                readerCompleted = true;
            }

            // Stop Streamlink with one graceful signal, then force-kill. Never signal the
            // process group and never use stdin EOF to stop it.
            if (!streamlinkTask.IsCompleted)
            {
                ProcessStop stop = await StopAsync(
                    streamlinkTask, streamlinkGraceful, streamlinkForce);
                if (stop == ProcessStop.TimedOut)
                    throw new TimeoutException("streamlink did not stop after a force kill");
                forcedKill |= stop == ProcessStop.Forced;
            }

            // Only now deliver EOF: it is FFmpeg's graceful finish.
            await pipe.Writer.CompleteAsync();

            if (!ffmpegTask.IsCompleted)
            {
                if (!await CompletesWithinAsync(
                        ffmpegTask, TimeSpan.FromSeconds(recorder.FinalizeTimeoutSeconds)))
                {
                    reason = $"{reason}; ffmpeg did not finalize";
                    finalizeTimedOut = true;
                    ProcessStop stop = await StopAsync(ffmpegTask, ffmpegGraceful, ffmpegForce);
                    if (stop == ProcessStop.TimedOut)
                        throw new TimeoutException("ffmpeg did not stop after a force kill");
                    forcedKill |= stop == ProcessStop.Forced;
                }
            }

            if (!readerCompleted)
                await pipe.Reader.CompleteAsync();
        }
        catch
        {
            // Never leave the other process running when the job fails unexpectedly.
            await streamlinkForce.CancelAsync();
            await ffmpegForce.CancelAsync();
            throw;
        }
        finally
        {
            await watchdog.CancelAsync();
            await watchdogTask;
        }

        if (PartFiles.HasSegments(session.Directory, job.Part)
            && Interlocked.Exchange(ref producing, 1) == 0)
        {
            // A very short part may produce only its final progress block, or no progress at all.
            await publish(
                new DvrEvent.PartProducingMedia(job, clock.GetUtcNow()),
                CancellationToken.None);
        }

        if (Volatile.Read(ref producing) == 0)
            PartFiles.Delete(session.Directory, job.Part);

        // The watchdog and force-kill paths beat a requested stop; an escalated finalize did not
        // end on its own, so it is reported as killed rather than as a self exit.
        RecorderExitKind kind = forcedKill || finalizeTimedOut || stalledEnd
            ? RecorderExitKind.Killed
            : stopRequested.IsCancellationRequested
                ? RecorderExitKind.Stopped
                : RecorderExitKind.Exited;

        string detail = $"{reason}; streamlink {Describe(streamlinkTask)}, ffmpeg {Describe(ffmpegTask)}";
        string stderr = tail.ToString();
        if (stderr.Length > 0)
            detail = $"{detail}; {stderr}";

        // Diagnostic text can include a tool exception message; redact once more at the boundary
        // so the subscriber token can never leak through the detail or the log.
        detail = Redact(detail);

        logger.LogInformation("Recorder {Job} finished ({Kind}): {Detail}", job, kind, detail);
        await publish(
            new DvrEvent.RecorderExited(
                job,
                clock.GetUtcNow(),
                kind,
                detail),
            CancellationToken.None);
    }

    /// <summary>
    /// Polls every five seconds and completes <paramref name="stalled"/> when neither fresh media
    /// nor an advertised ad-break grace window has moved the activity deadline forward.
    /// </summary>
    private async Task WatchForStallAsync(
        Func<long> lastActivityTicks,
        TimeSpan timeout,
        TaskCompletionSource stalled,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(WatchdogInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                long idleTicks = clock.GetUtcNow().UtcTicks - lastActivityTicks();
                if (idleTicks > timeout.Ticks)
                {
                    stalled.TrySetResult();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The job ended on its own; the watchdog is no longer needed.
        }
    }

    private static async Task<ProcessOutcome> RunProcessAsync(
        Command command,
        CancellationToken forceKill,
        CancellationToken gracefulStop)
    {
        try
        {
            CommandResult result = await command
                .ExecuteAsync(forceKill, gracefulStop)
                .Task
                .ConfigureAwait(false);
            return new ProcessOutcome(result.ExitCode, null, Cancelled: false);
        }
        catch (OperationCanceledException)
        {
            return new ProcessOutcome(null, null, Cancelled: true);
        }
        catch (Exception ex)
        {
            return new ProcessOutcome(null, ex, Cancelled: false);
        }
    }

    /// <summary>
    /// Stops one process within the bounded graceful then force-kill budget. CliWrap's
    /// graceful token signals the single child PID; it never signals the process group.
    /// </summary>
    private async Task<ProcessStop> StopAsync(
        Task process,
        CancellationTokenSource gracefulStop,
        CancellationTokenSource forceKill)
    {
        if (process.IsCompleted)
            return ProcessStop.AlreadyStopped;

        await gracefulStop.CancelAsync();
        if (await CompletesWithinAsync(process, GracefulStopTimeout))
            return ProcessStop.Graceful;

        await forceKill.CancelAsync();
        return await CompletesWithinAsync(process, ForceStopTimeout)
            ? ProcessStop.Forced
            : ProcessStop.TimedOut;
    }

    private async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
            return true;

        return await Task.WhenAny(task, Task.Delay(timeout, clock)).ConfigureAwait(false) == task;
    }

    private static string Describe(Task<ProcessOutcome> process)
    {
        if (!process.IsCompletedSuccessfully)
            return "still running";

        ProcessOutcome outcome = process.Result;
        if (outcome.Cancelled)
            return "stopped";
        if (outcome.Error is not null)
            return $"failed: {outcome.Error.Message}";
        return $"exit {outcome.ExitCode}";
    }

    private readonly record struct ProcessOutcome(int? ExitCode, Exception? Error, bool Cancelled);

    private enum ProcessStop
    {
        AlreadyStopped,
        Graceful,
        Forced,
        TimedOut,
    }
}
