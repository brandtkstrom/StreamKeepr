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
/// and it also refreshes after finalize/delete because both mutate the library. A sample raises
/// <see cref="Changed"/> only when free space moved by more than 1 % of the total (or the total
/// itself changed), so the meter does not churn on every library update.
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
    private StorageView _current;
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

        lock (_gate)
        {
            if (_disposed || !MovedEnough(_current, sample))
                return;

            _current = sample;
        }

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
        if (next.TotalBytes <= 0)
            return false;

        if (next.TotalBytes != previous.TotalBytes)
            return true;

        double threshold = next.TotalBytes * ChangeThreshold;
        return Math.Abs(next.FreeBytes - previous.FreeBytes) > threshold;
    }
}
