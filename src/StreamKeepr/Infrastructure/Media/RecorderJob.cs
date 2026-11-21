using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Telemetry;

namespace StreamKeepr.Infrastructure.Media;

/// <summary>
/// Runs Streamlink and FFmpeg for exactly one part of one session. The job publishes
/// <see cref="DvrEvent.PartProducingMedia"/> at most once and returns one
/// <see cref="PartOutcome"/> on every path, including an unverified shutdown and an unexpected
/// error after the tools started.
/// </summary>
public sealed class RecorderJob(
    IOptions<RecorderOptions> options,
    TimeProvider clock,
    ILogger<RecorderJob> logger,
    StreamKeeprTelemetry telemetry) : IRecorder
{
    private const int StderrCapacity = 20;

    private readonly IRecorderProcessRunner _processRunner = new CliWrapProcessRunner();
    private readonly Func<int?, DateTime?, ProcessIdentity> _identityProbe =
        ProcessIdentityInspector.Inspect;
    private readonly Func<string, int, long> _partBytesScan = PartFiles.TotalBytes;

    /// <summary>
    /// Test seam: production uses the CliWrap runner, the real process table, and a full scan of
    /// the part's files. Tests substitute them so an unverified stop or an unreadable part size is
    /// deterministic without long-lived processes.
    /// </summary>
    internal RecorderJob(
        IOptions<RecorderOptions> options,
        TimeProvider clock,
        ILogger<RecorderJob> logger,
        StreamKeeprTelemetry telemetry,
        IRecorderProcessRunner processRunner,
        Func<int?, DateTime?, ProcessIdentity>? identityProbe = null,
        Func<string, int, long>? partBytesScan = null)
        : this(options, clock, logger, telemetry)
    {
        _processRunner = processRunner;
        if (identityProbe is not null)
            _identityProbe = identityProbe;
        if (partBytesScan is not null)
            _partBytesScan = partBytesScan;
    }

    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ForceStopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Records the job's part into <see cref="Session.Directory"/>. A part that already has any
    /// file is never overwritten; the job returns an outcome without starting either process.
    /// A requested <paramref name="stopRequested"/> runs the bounded shutdown and reports
    /// <see cref="RecorderExitKind.Stopped"/> unless a force path was needed.
    /// </summary>
    public async Task<PartOutcome> RunAsync(
        Session session,
        JobId job,
        Func<DvrEvent, CancellationToken, ValueTask> publish,
        CancellationToken stopRequested = default,
        Action<JobId, long, TimeSpan>? reportProgress = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(publish);

        RecorderOptions recorder = options.Value;

        // One bounded scope per job, so every recorder line (including raw tool output) can be
        // correlated by channel, session, and job. It ends with the job; never hold it longer.
        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["Channel"] = session.Channel,
            ["SessionId"] = session.Id,
            ["Job"] = job.ToString(),
        });

        Directory.CreateDirectory(session.Directory);

        if (PartFiles.Exists(session.Directory, job.Part))
        {
            return new PartOutcome(
                RecorderExitKind.Exited,
                $"part {job.Part:D3} already exists; refusing to overwrite");
        }

        if (stopRequested.IsCancellationRequested)
            return new PartOutcome(RecorderExitKind.Stopped, "stop requested before start");

        var tail = new StderrTail(StderrCapacity);
        var parser = new FfmpegProgressParser();
        var byteTracker = new PartByteTracker(session.Directory, job.Part);
        int producing = 0;
        long lastMediaAdvanceTicks = clock.GetUtcNow().UtcTicks;
        long adGraceUntilTicks = 0;

        // Tool output may echo the subscriber token or carry line breaks and terminal controls;
        // redact and escape it before it reaches the tail, the detail, or any log.
        string ForLog(string line) => ProcessOutput.ForLog(line, recorder.TwitchUserOAuthToken);

        long startedAt = clock.GetTimestamp();
        logger.LogInformation(
            "Recorder {Job} started part {Part} for {Channel}",
            job,
            job.Part,
            session.Channel);

        // The bounded pipe is the backpressure boundary between the two tools: Streamlink
        // pauses at 4 MiB and resumes when FFmpeg has consumed back down to 2 MiB.
        Pipe pipe = RecorderPipe.Create();

        using var ffmpegGraceful = new CancellationTokenSource();
        using var ffmpegForce = new CancellationTokenSource();
        using var streamlinkGraceful = new CancellationTokenSource();
        using var streamlinkForce = new CancellationTokenSource();

        async ValueTask OnFfmpegStdout(string line)
        {
            if (parser.Feed(line) is not { } sample)
                return;

            // total_size is N/A for the HLS muxer. The tracker reads the part playlist
            // incrementally, so a progress sample never enumerates the part directory.
            long bytes = byteTracker.Sample();
            logger.LogDebug(
                "Recorder {Job} progress: {Bytes} bytes, media {MediaTime}",
                job,
                bytes,
                sample.MediaTime);
            reportProgress?.Invoke(job, bytes, sample.MediaTime);

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
        }

        void OnFfmpegStderr(string line)
        {
            string safe = ForLog(line);
            tail.Add("ffmpeg", safe);
            logger.LogDebug("[ffmpeg {Job}] {Line}", job, safe);
        }

        void OnStreamlinkStderr(string line)
        {
            string safe = ForLog(line);

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
        }

        var ffmpegLaunch = new ToolLaunch(
            "ffmpeg",
            recorder.FfmpegPath,
            FfmpegArgs.ForPart(session.Directory, job.Part, recorder.SegmentSeconds),
            session.Directory,
            StandardInput: pipe.Reader.AsStream());
        var streamlinkLaunch = new ToolLaunch(
            "streamlink",
            recorder.StreamlinkPath,
            StreamlinkArgs.For(session.Channel, recorder),
            session.Directory,
            StandardOutput: pipe.Writer.AsStream());

        RunningProcess ffmpeg = _processRunner.Start(
            ffmpegLaunch, OnFfmpegStdout, OnFfmpegStderr, ffmpegForce.Token, ffmpegGraceful.Token);
        RunningProcess streamlink = _processRunner.Start(
            streamlinkLaunch,
            onStdoutLine: null,
            OnStreamlinkStderr,
            streamlinkForce.Token,
            streamlinkGraceful.Token);
        Task<ProcessOutcome> ffmpegTask = ffmpeg.Completion;
        Task<ProcessOutcome> streamlinkTask = streamlink.Completion;

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

        // Assigned once the first task completes; the initial value names an earlier failure.
        string reason = "recorder failed before a tool ended";
        bool forcedKill = false;
        bool finalizeTimedOut = false;
        bool stalledEnd = false;
        ProcessStop streamlinkStop = ProcessStop.AlreadyStopped;
        ProcessStop ffmpegStop = ProcessStop.AlreadyStopped;

        // One authoritative scan for telemetry, the finish log, and their equality. A metadata
        // error must never fail the recorder; the size is then unknown, never a guessed number.
        long? ScanPartBytes()
        {
            try
            {
                return _partBytesScan(session.Directory, job.Part);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Could not read the part size; the tracked total was {TrackedBytes} bytes.",
                    byteTracker.Total);
                return null;
            }
        }

        string AppendTailAndRedact(string text)
        {
            string stderr = tail.ToString();
            if (stderr.Length > 0)
                text = $"{text}; {stderr}";

            // Diagnostic text can include a tool exception message; redact once more at the
            // boundary so the subscriber token can never leak through the detail or the log.
            return ProcessOutput.Redact(text, recorder.TwitchUserOAuthToken);
        }

        // Every path on which the tools ran ends here, including an unverified shutdown, so the
        // part is always counted and its finish is always logged.
        void ReportPart(RecorderExitKind kind, bool stall, bool forceKill, long? bytes, string detail)
        {
            telemetry.PartExited(kind, clock.GetElapsedTime(startedAt), bytes);
            if (stall)
                telemetry.Stall();
            if (forceKill)
                telemetry.ForceKill();

            // Bytes are what the part left on disk: zero when an empty part was removed, null
            // when the size could not be read. Only the log copy of the detail is escaped.
            double elapsedMs = clock.GetElapsedTime(startedAt).TotalMilliseconds;
            logger.LogInformation(
                "Recorder {Job} finished ({Kind}) after {ElapsedMs:F0} ms with {Bytes} bytes: {Detail}",
                job,
                kind,
                elapsedMs,
                bytes,
                ProcessOutput.Sanitize(detail));
        }

        UnverifiedShutdown? fault = null;
        Exception? unexpected = null;
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
                (streamlinkStop, fault) = await StopAsync(
                    "streamlink", streamlink, streamlinkGraceful, streamlinkForce);
                forcedKill |= streamlinkStop == ProcessStop.Forced;
            }

            // After an unverified Streamlink stop, FFmpeg is force-cancelled below instead of
            // being finalized: the previous writer may still be running.
            if (fault is null)
            {
                // Only now deliver EOF: it is FFmpeg's graceful finish.
                await pipe.Writer.CompleteAsync();

                if (!ffmpegTask.IsCompleted
                    && !await CompletesWithinAsync(
                        ffmpegTask, TimeSpan.FromSeconds(recorder.FinalizeTimeoutSeconds)))
                {
                    reason = $"{reason}; ffmpeg did not finalize";
                    finalizeTimedOut = true;
                    (ffmpegStop, fault) = await StopAsync(
                        "ffmpeg", ffmpeg, ffmpegGraceful, ffmpegForce);
                    forcedKill |= ffmpegStop == ProcessStop.Forced;
                }

                if (!readerCompleted)
                    await pipe.Reader.CompleteAsync();
            }
        }
        catch (Exception ex)
        {
            // An unexpected error must not skip verification, finalize, or the report: force
            // both tools and verify each stop exactly as the bounded shutdown would.
            unexpected = ex;
            logger.LogError(ex, "Recorder {Job} failed; force-stopping both tools", job);
            if (!streamlinkTask.IsCompleted)
            {
                (streamlinkStop, fault) = await ForceStopAsync("streamlink", streamlink, streamlinkForce);
                forcedKill |= streamlinkStop == ProcessStop.Forced;
            }

            if (!ffmpegTask.IsCompleted)
            {
                (ffmpegStop, UnverifiedShutdown? ffmpegFault) =
                    await ForceStopAsync("ffmpeg", ffmpeg, ffmpegForce);
                fault ??= ffmpegFault;
                forcedKill |= ffmpegStop == ProcessStop.Forced;
            }
        }
        finally
        {
            await watchdog.CancelAsync();
            await watchdogTask;
        }

        if (unexpected is not null)
            reason = $"{reason}; recorder failed: {unexpected.Message}";

        if (fault is not null)
        {
            // The writer may still be alive: force-cancel whatever is left and never delete part
            // files. The coordinator publishes the Killed exit and stops the host.
            await streamlinkForce.CancelAsync();
            await ffmpegForce.CancelAsync();
            string faultDetail = AppendTailAndRedact($"{reason}; {fault}");
            long? faultBytes = ScanPartBytes();
            ReportPart(RecorderExitKind.Killed, stalledEnd, forceKill: true, faultBytes, faultDetail);
            return new PartOutcome(
                RecorderExitKind.Killed,
                faultDetail,
                faultBytes,
                clock.GetElapsedTime(startedAt),
                Failed: true,
                Fault: fault);
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

        long? bytes = ScanPartBytes();

        // The watchdog and force-kill paths beat a requested stop; an escalated finalize did not
        // end on its own, so it is reported as killed rather than as a self exit.
        RecorderExitKind kind = forcedKill || finalizeTimedOut || stalledEnd
            ? RecorderExitKind.Killed
            : stopRequested.IsCancellationRequested
                ? RecorderExitKind.Stopped
                : RecorderExitKind.Exited;

        // The detail is display text, and a newline in it is harmless.
        string detail = AppendTailAndRedact(
            $"{reason}; streamlink {Describe(streamlink, streamlinkStop)}, "
            + $"ffmpeg {Describe(ffmpeg, ffmpegStop)}");

        ReportPart(kind, stalledEnd, forcedKill || finalizeTimedOut, bytes, detail);
        return new PartOutcome(
            kind,
            detail,
            bytes,
            clock.GetElapsedTime(startedAt),
            Failed: unexpected is not null);
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

    /// <summary>
    /// Stops one process within the bounded graceful then force-kill budget. CliWrap's graceful
    /// token signals the single child PID; it never signals the process group.
    /// </summary>
    private async Task<(ProcessStop Stop, UnverifiedShutdown? Fault)> StopAsync(
        string tool,
        RunningProcess process,
        CancellationTokenSource gracefulStop,
        CancellationTokenSource forceKill)
    {
        if (process.Completion.IsCompleted)
            return (ProcessStop.AlreadyStopped, null);

        await gracefulStop.CancelAsync();
        if (await CompletesWithinAsync(process.Completion, GracefulStopTimeout))
            return (ProcessStop.Graceful, null);

        return await ForceStopAsync(tool, process, forceKill);
    }

    /// <summary>
    /// Force-kills one process and verifies it. A force kill is accepted only when the captured
    /// PID is gone or was reused; a pending task or an unreadable identity is an unverified
    /// shutdown, because a descendant may still be writing.
    /// </summary>
    private async Task<(ProcessStop Stop, UnverifiedShutdown? Fault)> ForceStopAsync(
        string tool,
        RunningProcess process,
        CancellationTokenSource forceKill)
    {
        await forceKill.CancelAsync();
        if (!await CompletesWithinAsync(process.Completion, ForceStopTimeout))
        {
            ProcessIdentity observed = Probe(process);
            return (ProcessStop.TimedOut, Unverified(tool, process, ProcessStop.TimedOut, true, observed));
        }

        ProcessIdentity identity = Probe(process);
        return identity.IsVerifiedGone
            ? (ProcessStop.Forced, null)
            : (ProcessStop.Unverified, Unverified(tool, process, ProcessStop.Unverified, false, identity));
    }

    /// <summary>
    /// Reads the captured PID's identity. A probe that throws proves nothing, so it is an
    /// unknown identity and the stop fails closed.
    /// </summary>
    private ProcessIdentity Probe(RunningProcess process)
    {
        try
        {
            return _identityProbe(process.ProcessId, process.StartTime);
        }
        catch (Exception ex)
        {
            return new ProcessIdentity(
                ProcessIdentityKind.Unknown,
                process.ProcessId,
                process.StartTime,
                ObservedStartTime: null,
                $"identity could not be read: {ex.Message}");
        }
    }

    private static UnverifiedShutdown Unverified(
        string tool,
        RunningProcess process,
        ProcessStop stop,
        bool taskPending,
        ProcessIdentity observed) =>
        new(tool, process.ProcessId, process.StartTime, stop, taskPending, observed.Detail);

    private async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
            return true;

        return await Task.WhenAny(task, Task.Delay(timeout, clock)).ConfigureAwait(false) == task;
    }

    private static string Describe(RunningProcess process, ProcessStop stop)
    {
        Task<ProcessOutcome> completion = process.Completion;
        string identity =
            $"pid {FormatPid(process.ProcessId)} started {FormatStart(process.StartTime)}";
        if (!completion.IsCompletedSuccessfully)
            return $"{identity} still running (stop {stop})";

        ProcessOutcome outcome = completion.Result with { Stop = stop, TaskPending = false };
        string state = outcome.Cancelled
            ? "stopped"
            : outcome.Error is not null
                ? $"failed: {outcome.Error.Message}"
                : $"exit {outcome.ExitCode}";
        return $"pid {FormatPid(outcome.ProcessId)} started {FormatStart(outcome.StartTime)} "
            + $"{state} (stop {outcome.Stop}, task {(outcome.TaskPending ? "pending" : "completed")})";
    }

    private static string FormatPid(int? processId) => processId?.ToString() ?? "unknown";

    private static string FormatStart(DateTime? startTime) =>
        startTime?.ToString("O") ?? "unknown";
}
