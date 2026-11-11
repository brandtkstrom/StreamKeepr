using System.Threading.Channels;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// Owns the poll tick interval, burst window, nudge coalescing, and push coverage.
/// The poller supplies the normal and push-aware intervals; this type chooses between them.
/// </summary>
public sealed class PollSchedule(TimeProvider clock)
{
    private static readonly TimeSpan BurstInterval = TimeSpan.FromSeconds(10);

    private readonly Channel<bool> _nudge = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private volatile bool _pushConnected;
    private long _lastPollTicks = clock.GetUtcNow().UtcTicks;
    private long _burstUntilTicks;

    public bool IsPushConnected => _pushConnected;

    public DateTimeOffset LastPollAt =>
        new(Interlocked.Read(ref _lastPollTicks), TimeSpan.Zero);

    /// <summary>
    /// True only when every watched channel has both push subscriptions. A coverage change
    /// wakes a pending wait so losing push takes effect immediately.
    /// </summary>
    public void SetPushCoverage(bool allCovered)
    {
        if (_pushConnected == allCovered)
            return;

        bool lostCoverage = _pushConnected && !allCovered;
        _pushConnected = allCovered;

        // Losing push must shorten an in-flight 300 s wait; gaining it can wait for the
        // current tick to finish.
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
                : _pushConnected
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
