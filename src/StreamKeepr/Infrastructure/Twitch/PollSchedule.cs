using System.Threading.Channels;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>The reason push is or is not fully covering the watched channels.</summary>
public enum PushState
{
    PollingOnly,
    NoAdminToken,
    EventSubReconnecting,
    PartialCoverage,
    Connected,
}

/// <summary>One immutable push-status snapshot for the UI and health checks.</summary>
public sealed record PushStatus(PushState State)
{
    public static PushStatus PollingOnly { get; } = new(PushState.PollingOnly);
    public static PushStatus NoAdminToken { get; } = new(PushState.NoAdminToken);
    public static PushStatus Reconnecting { get; } = new(PushState.EventSubReconnecting);
    public static PushStatus PartialCoverage { get; } = new(PushState.PartialCoverage);
    public static PushStatus AllConnected { get; } = new(PushState.Connected);

    public bool Connected => State == PushState.Connected;

    /// <summary>All-or-nothing coverage (contract §4): connected only when every watched channel is covered.</summary>
    public static PushStatus FromCoverage(int covered, int watched)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(covered);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(covered, watched);
        return watched == 0 ? PollingOnly
            : covered == watched ? AllConnected
            : PartialCoverage;
    }

    /// <summary>The stable user-facing reason from backend-for-UI contract §3.</summary>
    public string Reason => State switch
    {
        PushState.Connected => "EventSub connected",
        PushState.NoAdminToken => "No admin token",
        PushState.PartialCoverage => "Partial coverage",
        PushState.EventSubReconnecting => "EventSub reconnecting",
        _ => "Polling only",
    };
}

/// <summary>
/// Owns the poll tick interval, burst window, nudge coalescing, and push coverage.
/// The poller supplies the normal and push-aware intervals; this type chooses between them.
/// </summary>
public sealed class PollSchedule(TimeProvider clock)
{
    private static readonly TimeSpan BurstInterval = TimeSpan.FromSeconds(10);

    private readonly Channel<bool> _nudge = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private volatile PushStatus _push = PushStatus.PollingOnly;
    private long _lastPollTicks = clock.GetUtcNow().UtcTicks;
    private long _burstUntilTicks;

    public PushStatus Push => _push;

    public bool IsPushConnected => _push.Connected;

    /// <summary>Raised whenever push coverage is recomputed.</summary>
    public event Action? Changed;

    public DateTimeOffset LastPollAt =>
        new(Interlocked.Read(ref _lastPollTicks), TimeSpan.Zero);

    /// <summary>
    /// Updates push coverage and its reason. Losing push wakes a pending wait so the normal
    /// polling interval takes effect immediately.
    /// </summary>
    public void SetPushStatus(PushStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        bool lostCoverage = _push.Connected && !status.Connected;
        _push = status;
        Changed?.Invoke();

        if (lostCoverage)
            Nudge();
    }

    /// <summary>Wake an in-progress wait. Multiple nudges coalesce into one.</summary>
    public void Nudge() => _nudge.Writer.TryWrite(true);

    /// <summary>Poll every 10 s for <paramref name="duration"/> after a push notification.</summary>
    public void Burst(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        Interlocked.Exchange(
            ref _burstUntilTicks, (clock.GetUtcNow() + duration).UtcTicks);

        // Shorten a pending normal/push wait to the burst cadence.
        Nudge();
    }

    public void Polled() =>
        Interlocked.Exchange(ref _lastPollTicks, clock.GetUtcNow().UtcTicks);

    /// <summary>
    /// Waits for the next tick. <paramref name="withPush"/> is used only when all channels
    /// are covered; a burst overrides both intervals. Nudge wakes the wait immediately.
    /// </summary>
    public async Task WaitAsync(
        TimeSpan normal, TimeSpan withPush, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(normal, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(withPush, TimeSpan.Zero);

        TimeSpan interval = clock.GetUtcNow().UtcTicks
            < Interlocked.Read(ref _burstUntilTicks)
                ? BurstInterval
                : _push.Connected
                    ? withPush
                    : normal;

        using var timeout = new CancellationTokenSource(interval, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            await _nudge.Reader.ReadAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timed out: a regular tick.
        }
    }
}
