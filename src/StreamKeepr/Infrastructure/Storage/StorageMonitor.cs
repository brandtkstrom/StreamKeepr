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
