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
}

/// <summary>Leases that keep a recording from being deleted while an export reads it.</summary>
public interface IExportLeaseRegistry
{
    IDisposable Acquire(string recordingId);

    bool IsLeased(string recordingId);
}

public sealed class ExportLeaseRegistry : IExportLeaseRegistry
{
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public IDisposable Acquire(string recordingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingId);
        lock (_gate)
            _leases[recordingId] = _leases.GetValueOrDefault(recordingId) + 1;
        return new Lease(this, recordingId);
    }

    public bool IsLeased(string recordingId)
    {
        lock (_gate)
            return _leases.ContainsKey(recordingId);
    }

    private void Release(string recordingId)
    {
        lock (_gate)
        {
            if (!_leases.TryGetValue(recordingId, out int count))
                return;
            if (count <= 1)
                _leases.Remove(recordingId);
            else
                _leases[recordingId] = count - 1;
        }
    }

    private sealed class Lease(ExportLeaseRegistry owner, string recordingId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release(recordingId);
        }
    }
}

/// <summary>
/// Singleton export queue: one FFmpeg stream copy at a time, FIFO, independent of any circuit.
/// Each job writes <c>.streamkeepr/exports/{id}.mp4</c> through a temp file and rename, reports
/// progress 0..1 from <c>out_time_us</c>, and holds a recording lease for its whole lifetime.
/// </summary>
public sealed class ExportService : BackgroundService, IExportService
{
    public static readonly TimeSpan ChangedThrottle = TimeSpan.FromMilliseconds(500);
    public const string ContentType = "video/mp4";
    internal const string FailedMessage = "The export could not be completed.";

    private readonly RecordingLibrary _library;
    private readonly IExportLeaseRegistry _leases;
    private readonly IOptions<StorageOptions> _storage;
    private readonly IOptions<RecorderOptions> _recorder;
    private readonly TimeProvider _clock;
    private readonly ILogger<ExportService> _logger;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly object _gate = new();
    private readonly Dictionary<string, ExportJob> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDisposable> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _latestByRecording = new(StringComparer.Ordinal);

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
            if (FindByRecordingLocked(recordingId) is { IsActive: true } active)
                return active;

            job = new ExportJob(Guid.NewGuid().ToString("N"), recordingId, ExportState.Queued, 0, null, null);
            _jobs[job.Id] = job;
            _latestByRecording[recordingId] = job.Id;
            _held[job.Id] = _leases.Acquire(recordingId);
        }

        _queue.Writer.TryWrite(job.Id);
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
        lock (_gate)
            return FindByRecordingLocked(recordingId);
    }

    public string? FilePath(string exportId)
    {
        if (Find(exportId) is not { State: ExportState.Succeeded })
            return null;

        string path = FinalPath(exportId);
        return File.Exists(path) ? path : null;
    }

    private ExportJob? FindByRecordingLocked(string recordingId)
    {
        if (!_latestByRecording.TryGetValue(recordingId, out string? id) || !_jobs.TryGetValue(id, out ExportJob? job))
            return null;

        return job.State == ExportState.Succeeded && !File.Exists(FinalPath(job.Id)) ? null : job;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (string id in _queue.Reader.ReadAllAsync(stoppingToken))
                await RunAsync(id, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown; queued jobs are in memory only and end with the process.
        }
    }

    private async Task RunAsync(string id, CancellationToken stoppingToken)
    {
        if (Find(id) is not { State: ExportState.Queued } job)
            return;

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

            Update(id, current => current with { State = ExportState.Running }, immediate: true);
            Directory.CreateDirectory(ExportsDirectory);

            double duration = summary.DurationSeconds is > 0 and double d ? d : 0;
            var parser = new FfmpegProgressParser();
            var stderr = new StringBuilder();

            await Cli.Wrap(_recorder.Value.FfmpegPath)
                .WithArguments(
                [
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-nostats", "-y",
                    "-i", index,
                    "-map", "0", "-c", "copy", "-movflags", "+faststart",
                    "-f", "mp4", "-progress", "pipe:1",
                    temp,
                ])
                .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
                {
                    if (parser.Feed(line) is { } sample)
                    {
                        double progress = ProgressFraction(sample.MediaTime, duration);
                        Update(id, current => current with { Progress = progress });
                    }
                }))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
                .ExecuteAsync(stoppingToken);

            File.Move(temp, FinalPath(id), overwrite: true);
            Update(
                id,
                current => current with { State = ExportState.Succeeded, Progress = 1, FileName = DownloadName(summary) },
                immediate: true);
            _logger.LogInformation("Export {ExportId} finished.", id);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            TryDelete(temp);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or CliWrap.Exceptions.CommandExecutionException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Export {ExportId} failed.", id);
            TryDelete(temp);
            Update(id, current => current with { State = ExportState.Failed, Error = FailedMessage }, immediate: true);
        }
        finally
        {
            ReleaseLease(id);
        }
    }

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
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete the partial export {Path}.", path);
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
