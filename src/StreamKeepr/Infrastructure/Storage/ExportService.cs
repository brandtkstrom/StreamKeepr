using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Threading.Channels;
using CliWrap;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Media;

namespace StreamKeepr.Infrastructure.Storage;

public enum ExportState { Queued, Running, Succeeded, Failed, Cancelled }

/// <summary>One export of a finished recording to a single MP4 (backend-for-UI contract §5).</summary>
public sealed record ExportJob(
    string Id, string RecordingId, ExportState State, double Progress,
    string? FileName, string? Error)
{
    public bool IsActive => State is ExportState.Queued or ExportState.Running;
}

public interface IExportService
{
    event Action? Changed;

    /// <summary>Queues an export, or returns the recording's active job if one exists.</summary>
    ExportJob Start(string recordingId, ClaimsPrincipal user, CancellationToken ct);

    ExportJob? Find(string exportId);

    /// <summary>
    /// The newest job for the recording, so the details card can rehydrate (E-22): the active
    /// job while Queued/Running, otherwise the latest result; null when there is none or a
    /// succeeded job's file is gone.
    /// </summary>
    ExportJob? FindByRecording(string recordingId);

    /// <summary>The finished file on disk, or null when the job is unknown or not finished.</summary>
    string? FilePath(string exportId);

    /// <summary>
    /// Removes a queued job or stops a running one, deleting its partial file and releasing the
    /// lease; the job ends Cancelled. Throws <see cref="KeyNotFoundException"/> for an unknown job
    /// and <see cref="InvalidOperationException"/> once it has finished.
    /// </summary>
    void Cancel(string exportId, ClaimsPrincipal user);
}

/// <summary>
/// Singleton export queue: one FFmpeg stream copy at a time, FIFO, independent of any circuit.
/// Each job is first probed (every part's video parameters must match), then writes
/// <c>.streamkeepr/exports/{id}.mp4</c> through a temp file and rename, reports progress 0..1
/// from <c>out_time_us</c>, and holds a recording lease for its whole lifetime. Export files
/// older than <see cref="MaxExportAge"/> are swept at startup and every
/// <see cref="CleanupInterval"/>.
/// </summary>
public sealed class ExportService : BackgroundService, IExportService
{
    public static readonly TimeSpan ChangedThrottle = TimeSpan.FromMilliseconds(500);
    public const string ContentType = "video/mp4";
    internal const string FailedMessage = "The export could not be completed.";

    /// <summary>The stable <see cref="ExportJob.Error"/> code for parts that cannot be joined.</summary>
    public const string IncompatibleParts = "ExportIncompatibleParts";

    public static readonly TimeSpan MaxExportAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    // A hung ffprobe must not stall every later export.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly RecordingLibrary _library;
    private readonly IExportLeaseRegistry _leases;
    private readonly IOptions<StorageOptions> _storage;
    private readonly IOptions<RecorderOptions> _recorder;
    private readonly TimeProvider _clock;
    private readonly ILogger<ExportService> _logger;
    // Jobs are probed in start order, then run in that same order: two single-reader stages.
    private readonly Channel<string> _probeQueue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly object _gate = new();
    private readonly Dictionary<string, ExportJob> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDisposable> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _latestByRecording = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    // Probed jobs whose video is HEVC and needs the hvc1 tag when they run.
    private readonly HashSet<string> _needsHvc1Tag = new(StringComparer.Ordinal);

    private DateTimeOffset _lastChanged = DateTimeOffset.MinValue;
    private ITimer? _changedTimer;

    public ExportService(
        RecordingLibrary library,
        IExportLeaseRegistry leases,
        IOptions<StorageOptions> storage,
        IOptions<RecorderOptions> recorder,
        TimeProvider clock,
        ILogger<ExportService> logger)
    {
        _library = library;
        _leases = leases;
        _storage = storage;
        _recorder = recorder;
        _clock = clock;
        _logger = logger;
    }

    public event Action? Changed;

    private string ExportsDirectory => AppDataPaths.ExportsDirectory(_storage.Value.RecordingsRoot);

    public ExportJob Start(string recordingId, ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingId);
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("Connect Twitch to export recordings.");

        RecordingSummary summary = _library.Find(recordingId)
            ?? throw new KeyNotFoundException("That recording is not in the library.");
        if (summary.IsRecording)
            throw new InvalidOperationException("The recording can be exported when it finishes.");

        ExportJob job;
        lock (_gate)
        {
            if (LatestLocked(recordingId) is { IsActive: true } active)
                return active;

            job = new ExportJob(Guid.NewGuid().ToString("N"), recordingId, ExportState.Queued, 0, null, null);

            // Acquire before publishing the job: a delete guard throws here, and no queued
            // job or stale lease entry may survive the refusal.
            IDisposable lease = _leases.Acquire(recordingId);
            _jobs[job.Id] = job;
            _latestByRecording[recordingId] = job.Id;
            _held[job.Id] = lease;
        }

        _probeQueue.Writer.TryWrite(job.Id);
        _logger.LogInformation("Queued export {ExportId} for {RecordingId}.", job.Id, recordingId);
        RaiseChanged(immediate: true);
        return job;
    }

    public ExportJob? Find(string exportId)
    {
        lock (_gate)
            return _jobs.GetValueOrDefault(exportId);
    }

    public ExportJob? FindByRecording(string recordingId)
    {
        ExportJob? job;
        lock (_gate)
            job = LatestLocked(recordingId);

        // The file check stays outside the lock; a cleaned-up export is no longer current.
        return job is { State: ExportState.Succeeded } && !File.Exists(FinalPath(job.Id)) ? null : job;
    }

    public string? FilePath(string exportId)
    {
        if (Find(exportId) is not { State: ExportState.Succeeded })
            return null;

        string path = FinalPath(exportId);
        return File.Exists(path) ? path : null;
    }

    public void Cancel(string exportId, ClaimsPrincipal user)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportId);
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("Connect Twitch to cancel exports.");

        lock (_gate)
        {
            ExportJob job = _jobs.GetValueOrDefault(exportId)
                ?? throw new KeyNotFoundException("That export no longer exists.");
            if (!job.IsActive)
                throw new InvalidOperationException("The export has already finished.");

            if (_running.TryGetValue(exportId, out CancellationTokenSource? running))
            {
                // The worker sees the cancellation, deletes the partial file, and ends the job.
                // Cancelling under the lock keeps RunAsync from disposing the source meanwhile;
                // the only callback is CliWrap's process kill, which never takes this lock.
                running.Cancel();
                return;
            }

            // Still queued (or being probed): the worker skips anything that is no longer Queued.
            _jobs[exportId] = job with { State = ExportState.Cancelled };
            _needsHvc1Tag.Remove(exportId);
        }

        ReleaseLease(exportId);
        _logger.LogInformation("Cancelled queued export {ExportId}.", exportId);
        RaiseChanged(immediate: true);
    }

    private ExportJob? LatestLocked(string recordingId) =>
        _latestByRecording.TryGetValue(recordingId, out string? id) ? _jobs.GetValueOrDefault(id) : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Created before the first await so the first periodic tick is never missed.
        using var cleanup = new PeriodicTimer(CleanupInterval, _clock);
        try
        {
            await Task.WhenAll(
                ProbeLoopAsync(stoppingToken),
                RunLoopAsync(stoppingToken),
                CleanupLoopAsync(cleanup, stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown; queued jobs are in memory only and end with the process.
        }
    }

    private async Task ProbeLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (string id in _probeQueue.Reader.ReadAllAsync(stoppingToken))
        {
            if (await ProbeAsync(id, stoppingToken))
                _queue.Writer.TryWrite(id);
        }
    }

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (string id in _queue.Reader.ReadAllAsync(stoppingToken))
            await RunAsync(id, stoppingToken);
    }

    private async Task CleanupLoopAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        await Task.Yield();
        do
        {
            SweepExports();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Checks that every part's video parameters match before the job may run. Incompatible
    /// parts fail the job with <see cref="IncompatibleParts"/> and no file; returns true when the
    /// job should be queued. A probe that cannot run leaves the decision to FFmpeg.
    /// </summary>
    private async Task<bool> ProbeAsync(string id, CancellationToken stoppingToken)
    {
        if (Find(id) is not { State: ExportState.Queued } job)
            return false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            if (_library.Find(job.RecordingId) is not { } summary
                || !RecordingsPath.TryResolve(
                    _storage.Value.RecordingsRoot,
                    [summary.Channel, summary.Id, SessionStore.IndexFileName],
                    out string index)
                || !File.Exists(index))
            {
                return true;
            }

            var parts = new List<PartVideo>();
            foreach (string init in ExportPartProbe.InitFiles(index))
                parts.Add(await ExportPartProbe.ProbeAsync(_recorder.Value.FfprobePath, init, timeout.Token));

            if (parts.Distinct().Count() > 1)
            {
                _logger.LogInformation(
                    "Export {ExportId} refused: the parts of {RecordingId} have different video parameters.",
                    id, job.RecordingId);
                FailQueued(id, IncompatibleParts);
                return false;
            }

            if (parts.Any(part => part.IsHevc))
            {
                lock (_gate)
                    _needsHvc1Tag.Add(id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not probe the parts for export {ExportId}; FFmpeg will decide.", id);
        }

        return true;
    }

    /// <summary>Removes export files (and leftover temp files) older than <see cref="MaxExportAge"/>.</summary>
    private void SweepExports()
    {
        try
        {
            SweepExportsCore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best effort and must keep running on the next tick.
            _logger.LogWarning(ex, "Could not sweep old export files.");
        }
    }

    private void SweepExportsCore()
    {
        if (!Directory.Exists(ExportsDirectory))
            return;

        DateTime cutoff = (_clock.GetUtcNow() - MaxExportAge).UtcDateTime;
        HashSet<string> active;
        lock (_gate)
            active = _jobs.Values.Where(job => job.IsActive).Select(job => job.Id).ToHashSet(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(ExportsDirectory).ToArray())
        {
            string name = Path.GetFileName(file);
            string id = name.Split('.', 2)[0];
            if (active.Contains(id) || File.GetLastWriteTimeUtc(file) >= cutoff)
                continue;

            TryDelete(file);
        }
    }

    private async Task RunAsync(string id, CancellationToken stoppingToken)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        ExportJob job;
        bool hevc;
        lock (_gate)
        {
            // A job cancelled while queued never runs.
            if (_jobs.GetValueOrDefault(id) is not { State: ExportState.Queued } queued)
                return;

            job = queued with { State = ExportState.Running };
            _jobs[id] = job;
            _running[id] = cancel;
            hevc = _needsHvc1Tag.Remove(id);
        }

        RaiseChanged(immediate: true);
        string temp = Path.Combine(ExportsDirectory, $"{id}.mp4.tmp");
        try
        {
            RecordingSummary summary = _library.Find(job.RecordingId)
                ?? throw new FileNotFoundException("The recording was deleted before its export ran.");
            if (!RecordingsPath.TryResolve(
                    _storage.Value.RecordingsRoot,
                    [summary.Channel, summary.Id, SessionStore.IndexFileName],
                    out string index)
                || !File.Exists(index))
            {
                throw new FileNotFoundException("The recording's playlist is missing.");
            }

            Directory.CreateDirectory(ExportsDirectory);

            double duration = summary.DurationSeconds is > 0 and double d ? d : 0;
            var parser = new FfmpegProgressParser();
            var stderr = new StringBuilder();

            await Cli.Wrap(_recorder.Value.FfmpegPath)
                .WithArguments(Arguments(index, temp, hevc))
                .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
                {
                    if (parser.Feed(line) is { } sample)
                    {
                        double progress = ProgressFraction(sample.MediaTime, duration);
                        Update(id, current => current with { Progress = progress });
                    }
                }))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
                .ExecuteAsync(cancel.Token);

            File.Move(temp, FinalPath(id), overwrite: true);
            Finish(id, current => current with { State = ExportState.Succeeded, Progress = 1, FileName = DownloadName(summary) });
            _logger.LogInformation("Export {ExportId} finished.", id);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            TryDelete(temp);
            throw;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            TryDelete(temp);
            Finish(id, current => current with { State = ExportState.Cancelled });
            _logger.LogInformation("Cancelled running export {ExportId}.", id);
        }
        catch (Exception ex)
        {
            // Any failure ends this job, never the worker: the next queued job still runs.
            _logger.LogWarning(ex, "Export {ExportId} failed.", id);
            TryDelete(temp);
            Finish(id, current => current with { State = ExportState.Failed, Error = FailedMessage });
        }
        finally
        {
            // Idempotent: also covers host shutdown, which publishes no finished state.
            lock (_gate)
                _running.Remove(id);
            ReleaseLease(id);
        }
    }

    /// <summary>
    /// Publishes a finished state only after the lease is gone, so anyone reacting to the change
    /// (a Delete enabled by it, a test) never finds the recording still leased.
    /// </summary>
    private void Finish(string id, Func<ExportJob, ExportJob> change)
    {
        ReleaseLease(id);
        Update(id, change, immediate: true);
    }

    /// <summary>
    /// The stream-copy command line. HEVC is tagged <c>hvc1</c> so Apple players and browsers
    /// recognize the MP4 (E-10); FFmpeg's default <c>hev1</c> tag is often refused.
    /// </summary>
    private static IReadOnlyList<string> Arguments(string index, string output, bool hevc) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", "-nostats", "-y",
        "-i", index,
        "-map", "0", "-c", "copy",
        .. (hevc ? new[] { "-tag:v", "hvc1" } : Array.Empty<string>()),
        "-movflags", "+faststart",
        "-f", "mp4", "-progress", "pipe:1",
        output,
    ];

    /// <summary>Fails a job that has not started running; a job cancelled meanwhile stays Cancelled.</summary>
    private void FailQueued(string id, string error) =>
        Finish(id, current => current.State == ExportState.Queued ? current with { State = ExportState.Failed, Error = error } : current);

    /// <summary>FFmpeg's <c>out_time_us</c> over the recording duration, clamped to 0..1.</summary>
    public static double ProgressFraction(TimeSpan mediaTime, double durationSeconds) =>
        durationSeconds > 0 && double.IsFinite(durationSeconds)
            ? Math.Clamp(mediaTime.TotalSeconds / durationSeconds, 0, 1)
            : 0;

    /// <summary>A readable attachment name: channel, local start time, and a safe title.</summary>
    public static string DownloadName(RecordingSummary summary)
    {
        var title = new StringBuilder();
        foreach (char c in summary.Title)
        {
            if (char.IsLetterOrDigit(c))
                title.Append(c);
            else if (title.Length > 0 && title[^1] != '-')
                title.Append('-');
            if (title.Length >= 60)
                break;
        }

        string slug = title.ToString().Trim('-');
        string stamp = summary.StartedAt.UtcDateTime.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return slug.Length > 0
            ? $"{summary.Channel}_{stamp}_{slug}.mp4"
            : $"{summary.Channel}_{stamp}.mp4";
    }

    private string FinalPath(string id) => Path.Combine(ExportsDirectory, $"{id}.mp4");

    private void Update(string id, Func<ExportJob, ExportJob> change, bool immediate = false)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out ExportJob? job))
                return;
            _jobs[id] = change(job);
        }

        RaiseChanged(immediate);
    }

    private void ReleaseLease(string id)
    {
        IDisposable? lease;
        lock (_gate)
            _held.Remove(id, out lease);

        lease?.Dispose();
    }

    /// <summary>
    /// State changes notify at once; progress notifies at most every 500 ms, with a trailing
    /// notification so the last value is never lost.
    /// </summary>
    private void RaiseChanged(bool immediate)
    {
        bool now;
        lock (_gate)
        {
            DateTimeOffset at = _clock.GetUtcNow();
            now = immediate || at - _lastChanged >= ChangedThrottle;
            if (now)
            {
                _lastChanged = at;
                _changedTimer?.Dispose();
                _changedTimer = null;
            }
            else
            {
                _changedTimer ??= _clock.CreateTimer(
                    _ => RaiseChanged(immediate: true),
                    null,
                    ChangedThrottle - (at - _lastChanged),
                    Timeout.InfiniteTimeSpan);
            }
        }

        if (now)
            Changed?.Invoke();
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete the export file {Path}.", path);
        }
    }

    public override void Dispose()
    {
        lock (_gate)
        {
            _changedTimer?.Dispose();
            _changedTimer = null;
            foreach (IDisposable lease in _held.Values)
                lease.Dispose();
            _held.Clear();
        }

        base.Dispose();
    }
}
