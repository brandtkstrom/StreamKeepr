namespace StreamKeepr.Ui;

/// <summary>One queued snackbar message with at most one action.</summary>
public sealed record SnackbarMessage(
    string Text,
    string? ActionLabel = null,
    Func<Task>? Action = null,
    TimeSpan? Duration = null);

/// <summary>
/// Scoped snackbar queue. Messages are shown one at a time and auto-dismiss;
/// <see cref="Current"/> and <see cref="Changed"/> are the only way the host
/// observes the queue.
/// </summary>
public sealed class SnackbarService : IDisposable
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _clock;
    private readonly Queue<SnackbarMessage> _pending = new();
    private readonly object _gate = new();

    private SnackbarMessage? _current;
    private ITimer? _timer;
    private long _generation;
    private bool _disposed;

    public SnackbarService(TimeProvider clock) => _clock = clock;

    public SnackbarMessage? Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public event Action? Changed;

    public void Show(
        string text,
        string? actionLabel = null,
        Func<Task>? action = null,
        TimeSpan? duration = null) =>
        Show(new SnackbarMessage(text, actionLabel, action, duration));

    public void Show(SnackbarMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Validate(message);

        var started = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_current is null)
            {
                StartCore(message);
                started = true;
            }
            else
            {
                _pending.Enqueue(message);
            }
        }

        if (started)
            Changed?.Invoke();
    }

    public void Dismiss()
    {
        bool changed;
        lock (_gate)
            changed = DismissCore();

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Runs the current message's action and dismisses it first.</summary>
    public async Task InvokeActionAsync()
    {
        SnackbarMessage? message;
        lock (_gate)
            message = _current;

        if (message is null)
            return;

        Dismiss();

        if (message.Action is not null)
            await message.Action();
    }

    private static void Validate(SnackbarMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Text))
            throw new ArgumentException("Snackbar text is required.", nameof(message));

        if ((message.ActionLabel is null) != (message.Action is null))
            throw new ArgumentException(
                "A snackbar action needs both a label and a callback.", nameof(message));

        if (message.ActionLabel is not null && string.IsNullOrWhiteSpace(message.ActionLabel))
            throw new ArgumentException("Snackbar action label is required.", nameof(message));

        if ((message.Duration ?? DefaultDuration) <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(message), "Snackbar duration must be positive.");
    }

    private void StartCore(SnackbarMessage message)
    {
        _current = message;
        _timer?.Dispose();

        // A disposed timer can still have a callback in flight. The generation
        // check keeps that stale callback from dismissing the next message.
        var generation = ++_generation;
        _timer = _clock.CreateTimer(
            _ => AutoDismiss(generation),
            null,
            message.Duration ?? DefaultDuration,
            Timeout.InfiniteTimeSpan);
    }

    private void AutoDismiss(long generation)
    {
        bool changed;
        lock (_gate)
        {
            if (generation != _generation)
                return;

            changed = DismissCore();
        }

        if (changed)
            Changed?.Invoke();
    }

    private bool DismissCore()
    {
        if (_current is null)
            return false;

        _timer?.Dispose();
        _timer = null;

        if (_pending.Count > 0)
            StartCore(_pending.Dequeue());
        else
            _current = null;

        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _current = null;
            _pending.Clear();
        }
    }
}
