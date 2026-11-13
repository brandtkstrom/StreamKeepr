namespace StreamKeepr.Ui;

/// <summary>
/// Coalesces bursts of source changes into one <see cref="Fired"/> per window (backend-for-UI
/// contract: view <c>Changed</c> events are throttled to 500 ms). Thread-safe.
/// </summary>
internal sealed class ThrottledSignal(TimeProvider clock) : IDisposable
{
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private ITimer? _timer;
    private bool _disposed;

    public event Action? Fired;

    /// <summary>Schedules one <see cref="Fired"/> unless one is already pending.</summary>
    public void Signal()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null)
                return;

            _timer = clock.CreateTimer(_ => Flush(), null, Window, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed)
                return;
        }

        Fired?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
