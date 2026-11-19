using StreamKeepr.Ui;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>Free and total bytes for the recordings volume, sampled by the UI layer.</summary>
public interface IStorageMonitor
{
    StorageView Current { get; }

    event Action? Changed;

    /// <summary>Samples immediately; call after delete/export or any media write.</summary>
    void Refresh();
}

/// <summary>
/// Singleton storage sampler. It reads <see cref="DiskGuard"/> at construction and every 60 s,
/// and it also refreshes after finalize/delete because both mutate the library.
/// <see cref="Current"/> always exposes the latest successful sample; <see cref="Changed"/> is
/// raised only when the sample moved by more than 1 % of the total (or the total itself changed)
/// since the last notification. Sampling and commit happen under one lock so a timer tick and a
/// library refresh cannot commit out of order.
/// It also logs each crossing of <c>Storage:MinFreeGb</c>, judged the way <see cref="DiskGuard"/>
/// judges it: one <c>Warning</c> when free space drops below the minimum (including a first sample
/// that is already low) and one <c>Information</c> when it recovers, never one per sample.
/// </summary>
public sealed class StorageMonitor : IStorageMonitor, IDisposable
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(60);

    /// <summary>The minimum move, as a fraction of total bytes, that raises a change.</summary>
    public const double ChangeThreshold = 0.01;

    private readonly DiskGuard _disk;
    private readonly RecordingLibrary _library;
    private readonly TimeProvider _clock;
    private readonly ILogger<StorageMonitor> _logger;
    private readonly object _gate = new();

    private ITimer? _timer;

    /// <summary>The most recent successful sample, including sub-threshold moves.</summary>
    private StorageView _current;

    /// <summary>The sample at which <see cref="Changed"/> last fired.</summary>
    private StorageView _notified;

    /// <summary>Whether the last successful sample had room to record; starts optimistic.</summary>
    private bool _hasRoom = true;
    private bool _disposed;

    public StorageMonitor(
        DiskGuard disk,
        RecordingLibrary library,
        TimeProvider clock,
        ILogger<StorageMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _disk = disk;
        _library = library;
        _clock = clock;
        _logger = logger;
        _current = Sample();
        _notified = _current;
        TrackThreshold(_current);

        _timer = _clock.CreateTimer(
            _ => Refresh(),
            state: null,
            SampleInterval,
            SampleInterval);

        // Finalize upserts and delete removes; both change the media footprint.
        _library.Changed += Refresh;
    }

    public StorageView Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public event Action? Changed;

    /// <summary>
    /// Samples immediately. Called by the timer and after finalize/delete via the library event;
    /// an export implementation calls this after its file is written. Safe from any thread.
    /// </summary>
    public void Refresh()
    {
        StorageView sample;
        bool notify;

        lock (_gate)
        {
            if (_disposed)
                return;

            try
            {
                sample = Sample();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                // The recordings volume can disappear or be remounted while the app runs.
                // Keep the last good sample and let the next tick try again.
                _logger.LogWarning(ex, "Could not sample free space for the recordings volume.");
                return;
            }

            // Current tracks every successful sample; the notification compares against the
            // last sample that actually crossed the threshold, not the last sampled value.
            _current = sample;
            TrackThreshold(sample);
            notify = MovedEnough(_notified, sample);
            if (notify)
                _notified = sample;
        }

        if (notify)
            Changed?.Invoke();
    }

    public void Dispose()
    {
        ITimer? timer;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            timer = _timer;
            _timer = null;
        }

        _library.Changed -= Refresh;
        timer?.Dispose();
    }

    private StorageView Sample()
    {
        long free = _disk.FreeBytes;
        long total = _disk.TotalBytes;
        return new StorageView(Math.Max(0, free), Math.Max(0, total));
    }

    /// <summary>Logs only when the sample lands on the other side of the minimum. Caller holds the gate.</summary>
    private void TrackThreshold(StorageView sample)
    {
        int minFreeGb = _disk.MinFreeGb;
        bool hasRoom = DiskGuard.HasRoom(sample.FreeBytes, minFreeGb);
        if (hasRoom == _hasRoom)
            return;

        _hasRoom = hasRoom;
        double freeGb = ToGb(sample.FreeBytes);
        double totalGb = ToGb(sample.TotalBytes);
        if (hasRoom)
        {
            _logger.LogInformation(
                "Free space on the recordings volume recovered to {FreeGb} GB of {TotalGb} GB (minimum {MinFreeGb} GB); new parts can start.",
                freeGb, totalGb, minFreeGb);
        }
        else
        {
            _logger.LogWarning(
                "Free space on the recordings volume dropped to {FreeGb} GB of {TotalGb} GB, below the {MinFreeGb} GB minimum; no new part will start until it recovers.",
                freeGb, totalGb, minFreeGb);
        }
    }

    private static double ToGb(long bytes) => Math.Round((double)bytes / DiskGuard.BytesPerGib, 2);

    private static bool MovedEnough(StorageView previous, StorageView next)
    {
        if (next.TotalBytes != previous.TotalBytes)
            return true;

        if (next.TotalBytes <= 0)
            return false;

        double threshold = next.TotalBytes * ChangeThreshold;
        return Math.Abs(next.FreeBytes - previous.FreeBytes) > threshold;
    }
}
