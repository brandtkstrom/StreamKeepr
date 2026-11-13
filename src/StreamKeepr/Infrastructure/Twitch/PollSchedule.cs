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

/// <summary>
/// One immutable push-status snapshot for the UI and health checks: the reason, how many
/// channels are watched, and which of them (by login) have both push subscriptions.
/// Equality compares the state, the watched count, and the covered set by value.
/// </summary>
public sealed record PushStatus(PushState State)
{
    private static readonly IReadOnlySet<string> None =
        new HashSet<string>(StringComparer.Ordinal);

    public static PushStatus PollingOnly { get; } = new(PushState.PollingOnly);
    public static PushStatus NoAdminToken { get; } = new(PushState.NoAdminToken);
    public static PushStatus Reconnecting { get; } = new(PushState.EventSubReconnecting);
    public static PushStatus PartialCoverage { get; } = new(PushState.PartialCoverage);
    public static PushStatus AllConnected { get; } = new(PushState.Connected);

    /// <summary>Logins with active stream.online and stream.offline subscriptions.</summary>
    public IReadOnlySet<string> CoveredLogins { get; init; } = None;

    /// <summary>Watched channels when coverage was last computed; 0 when unknown.</summary>
    public int Watched { get; init; }

    public int Covered => CoveredLogins.Count;

    /// <summary>The reason a fully covered channel reports.</summary>
    public const string ConnectedReason = "EventSub connected";

    public bool Connected => State == PushState.Connected;

    /// <summary>
    /// An EventSub session is up and has reported coverage (full or partial). False when
    /// coverage was computed without a socket, for example while no channel is resolved.
    /// </summary>
    public bool HasSession =>
        _sessionOpen && State is PushState.Connected or PushState.PartialCoverage;

    private bool _sessionOpen = true;

    public bool Covers(string login) => CoveredLogins.Contains(login);

    /// <summary>All-or-nothing coverage (contract §4): connected only when every watched channel is covered.</summary>
    /// <param name="sessionOpen">False when no EventSub socket is open (nothing could be subscribed).</param>
    public static PushStatus FromCoverage(
        IEnumerable<string> coveredLogins, int watched, bool sessionOpen = true)
    {
        ArgumentNullException.ThrowIfNull(coveredLogins);
        var covered = new HashSet<string>(coveredLogins, StringComparer.Ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(covered.Count, watched);
        PushState state = watched == 0 ? PushState.PollingOnly
            : covered.Count == watched ? PushState.Connected
            : PushState.PartialCoverage;
        return new PushStatus(state)
        {
            CoveredLogins = covered,
            Watched = watched,
            _sessionOpen = sessionOpen,
        };
    }

    public bool Equals(PushStatus? other) =>
        other is not null
        && State == other.State
        && Watched == other.Watched
        && HasSession == other.HasSession
        && CoveredLogins.SetEquals(other.CoveredLogins);

    public override int GetHashCode() => HashCode.Combine(State, Watched, Covered);

    /// <summary>The stable user-facing reason from backend-for-UI contract §3.</summary>
    public string Reason => State switch
    {
        PushState.Connected => ConnectedReason,
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

    // Liveness detail for the Settings page. 0 means "not yet"; none of these raise Changed.
    private long _lastEventSubMessageTicks;
    private long _lastSuccessfulPollTicks;
    private long _lastApiRoundTripTicks = -1;
    private long _currentIntervalTicks;

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

    /// <summary>
    /// A poll attempt finished. Health uses <see cref="LastPollAt"/> for liveness. A successful
    /// poll (every Helix <c>/streams</c> batch answered, or nothing to ask) also stamps
    /// <see cref="LastSuccessfulPollAt"/> with the same instant; <paramref name="roundTrip"/> is
    /// the Helix time when requests were made.
    /// </summary>
    public void Polled(bool succeeded = true, TimeSpan? roundTrip = null)
    {
        if (roundTrip is { } elapsed)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
            Interlocked.Exchange(ref _lastApiRoundTripTicks, elapsed.Ticks);
        }

        long now = clock.GetUtcNow().UtcTicks;
        if (succeeded)
            Interlocked.Exchange(ref _lastSuccessfulPollTicks, now);
        Interlocked.Exchange(ref _lastPollTicks, now);
    }

    /// <summary>Any EventSub message arrived, keepalives included.</summary>
    public void EventSubMessageReceived() =>
        Interlocked.Exchange(ref _lastEventSubMessageTicks, clock.GetUtcNow().UtcTicks);

    public DateTimeOffset? LastSuccessfulPollAt => At(ref _lastSuccessfulPollTicks);

    public DateTimeOffset? LastEventSubMessageAt => At(ref _lastEventSubMessageTicks);

    /// <summary>Round trip of the last fully successful poll's Helix requests.</summary>
    public TimeSpan? LastApiRoundTrip =>
        Interlocked.Read(ref _lastApiRoundTripTicks) is var ticks and >= 0
            ? TimeSpan.FromTicks(ticks)
            : null;

    /// <summary>The interval the most recent <see cref="WaitAsync"/> chose; null before the first.</summary>
    public TimeSpan? CurrentInterval =>
        Interlocked.Read(ref _currentIntervalTicks) is var ticks and > 0
            ? TimeSpan.FromTicks(ticks)
            : null;

    private static DateTimeOffset? At(ref long ticks) =>
        Interlocked.Read(ref ticks) is var value and > 0
            ? new DateTimeOffset(value, TimeSpan.Zero)
            : null;

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
        Interlocked.Exchange(ref _currentIntervalTicks, interval.Ticks);

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
