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
    private const int PipePauseThreshold = 4 << 20;
    private const int PipeResumeThreshold = 2 << 20;
    private const int StderrCapacity = 20;

    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ForceStopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Records the job's part into <see cref="Session.Directory"/>. A part that already has any
    /// file is never overwritten; the job reports an exit without starting either process.
    /// </summary>
    public async Task RunAsync(
        Session session,
        JobId job,
        Func<DvrEvent, CancellationToken, ValueTask> publish)
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

        var tail = new StderrTail(StderrCapacity);
        var parser = new FfmpegProgressParser();
        int producing = 0;

        string Redact(string line) =>
            string.IsNullOrEmpty(recorder.TwitchUserOAuthToken)
                ? line
                : line.Replace(recorder.TwitchUserOAuthToken, "***", StringComparison.Ordinal);

        // The bounded pipe is the backpressure boundary between the two tools: Streamlink
        // pauses at 4 MiB and resumes when FFmpeg has consumed back down to 2 MiB.
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: PipePauseThreshold,
            resumeWriterThreshold: PipeResumeThreshold));

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

                if (sample.Advanced && Interlocked.Exchange(ref producing, 1) == 0)
                {
                    await publish(
                        new DvrEvent.PartProducingMedia(job, clock.GetUtcNow()),
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
                tail.Add("streamlink", safe);
                logger.LogDebug("[streamlink {Job}] {Line}", job, safe);
            }));

        Task<ProcessOutcome> ffmpegTask = RunProcessAsync(
            ffmpegCommand, ffmpegForce.Token, ffmpegGraceful.Token);
        Task<ProcessOutcome> streamlinkTask = RunProcessAsync(
            streamlinkCommand, streamlinkForce.Token, streamlinkGraceful.Token);

        string reason;
        try
        {
            Task first = await Task.WhenAny(streamlinkTask, ffmpegTask);

            if (first == ffmpegTask)
            {
                // Release the reader first so Streamlink cannot block on a full pipe while
                // it waits for a graceful stop.
                reason = "ffmpeg exited first";
                await pipe.Reader.CompleteAsync();
                if (!await StopAsync(streamlinkTask, streamlinkGraceful, streamlinkForce))
                    throw new TimeoutException("streamlink did not stop after a force kill");
                await pipe.Writer.CompleteAsync();
            }
            else
            {
                // Stream end: close stdin so FFmpeg flushes the last segment and writes ENDLIST.
                reason = "streamlink exited";
                await pipe.Writer.CompleteAsync();
                if (!await CompletesWithinAsync(
                        ffmpegTask, TimeSpan.FromSeconds(recorder.FinalizeTimeoutSeconds)))
                {
                    reason = "ffmpeg did not finalize after stream end";
                    if (!await StopAsync(ffmpegTask, ffmpegGraceful, ffmpegForce))
                        throw new TimeoutException("ffmpeg did not stop after a force kill");
                }

                await pipe.Reader.CompleteAsync();
            }
        }
        catch
        {
            // Never leave the other process running when the job fails unexpectedly.
            await streamlinkForce.CancelAsync();
            await ffmpegForce.CancelAsync();
            throw;
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

        string detail = $"{reason}; streamlink {Describe(streamlinkTask)}, ffmpeg {Describe(ffmpegTask)}";
        string stderr = tail.ToString();
        if (stderr.Length > 0)
            detail = $"{detail}; {stderr}";

        logger.LogInformation("Recorder {Job} finished: {Detail}", job, detail);
        await publish(
            new DvrEvent.RecorderExited(
                job,
                clock.GetUtcNow(),
                RecorderExitKind.Exited,
                detail),
            CancellationToken.None);
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

    /// <summary>Stops one process within the bounded graceful then force-kill budget.</summary>
    private static async Task<bool> StopAsync(
        Task process,
        CancellationTokenSource gracefulStop,
        CancellationTokenSource forceKill)
    {
        if (process.IsCompleted)
            return true;

        await gracefulStop.CancelAsync();
        if (await CompletesWithinAsync(process, GracefulStopTimeout))
            return true;

        await forceKill.CancelAsync();
        return await CompletesWithinAsync(process, ForceStopTimeout);
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
            return true;

        return await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) == task;
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
}
