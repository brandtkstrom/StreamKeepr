using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Ui;

/// <summary>Whether EventSub has a live session, derived from <see cref="PushState"/>.</summary>
public enum EventSubConnection { Connected, Reconnecting, WaitingForAdmin, Off }

/// <summary>One snapshot of the Settings page's Connection status section.</summary>
/// <param name="Covered">Watched channels with both push subscriptions.</param>
/// <param name="Now">The clock reading the relative times are measured against.</param>
/// <param name="PollInterval">The cadence the poller last chose; null before its first wait.</param>
/// <param name="UncoveredPollInterval">The cadence used while any channel lacks push.</param>
public sealed record ConnectionStatus(
    string Transport,
    EventSubConnection Connection,
    int Covered,
    int Watched,
    HealthStatus Health,
    string? HealthDescription,
    DateTimeOffset Now,
    DateTimeOffset? LastMessageAt,
    DateTimeOffset LastPollAt,
    DateTimeOffset? LastSuccessfulPollAt,
    TimeSpan? ApiRoundTrip,
    TimeSpan? PollInterval,
    TimeSpan UncoveredPollInterval);

/// <summary>
/// The circuit's read-only connection status (backend-for-UI contract §3a). Admin-only:
/// <see cref="GetAsync"/> throws <see cref="UnauthorizedAccessException"/> otherwise.
/// <see cref="Changed"/> fires on push-status changes (throttled) and on sign-in changes; the
/// liveness timestamps change too often for events, so the UI re-reads on a timer.
/// </summary>
public interface IConnectionStatusView : IDisposable
{
    event Action? Changed;

    Task<ConnectionStatus> GetAsync(CancellationToken ct);
}

public sealed class ConnectionStatusView : IConnectionStatusView
{
    private readonly PollSchedule _schedule;
    private readonly IEventSubClient _eventSub;
    private readonly HealthCheckService _health;
    private readonly IOptions<TwitchOptions> _options;
    private readonly AuthenticationStateProvider _auth;
    private readonly TimeProvider _clock;
    private readonly ILogger<ConnectionStatusView> _logger;
    private readonly ThrottledSignal _throttle;

    private volatile Task<AuthenticationState> _authState;
    private int _disposed;

    public ConnectionStatusView(
        PollSchedule schedule,
        IEventSubClient eventSub,
        HealthCheckService health,
        IOptions<TwitchOptions> options,
        AuthenticationStateProvider auth,
        TimeProvider clock,
        ILogger<ConnectionStatusView> logger)
    {
        _schedule = schedule;
        _eventSub = eventSub;
        _health = health;
        _options = options;
        _auth = auth;
        _clock = clock;
        _logger = logger;
        _throttle = new ThrottledSignal(clock);
        _throttle.Fired += OnThrottleFired;

        _authState = auth.GetAuthenticationStateAsync();
        _auth.AuthenticationStateChanged += OnAuthenticationStateChanged;
        _schedule.Changed += OnScheduleChanged;
    }

    public event Action? Changed;

    public async Task<ConnectionStatus> GetAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        AdminGuard.Require(_authState);

        PushStatus push = _schedule.Push;
        (HealthStatus health, string? description) = await CheckHealthAsync(ct);

        return new ConnectionStatus(
            _eventSub.Transport,
            ConnectionOf(push),
            push.Covered,
            push.Watched,
            health,
            description,
            _clock.GetUtcNow(),
            _schedule.LastEventSubMessageAt,
            _schedule.LastPollAt,
            _schedule.LastSuccessfulPollAt,
            _schedule.LastApiRoundTrip,
            _schedule.CurrentInterval,
            TimeSpan.FromSeconds(_options.Value.PollIntervalSeconds));
    }

    internal static EventSubConnection ConnectionOf(PushStatus push) =>
        push.HasSession ? EventSubConnection.Connected
        : push.State switch
        {
            PushState.EventSubReconnecting => EventSubConnection.Reconnecting,
            PushState.NoAdminToken => EventSubConnection.WaitingForAdmin,
            _ => EventSubConnection.Off,
        };

    /// <summary>The same verdict as <c>/health</c>'s DVR check, so the page never disagrees with it.</summary>
    private async Task<(HealthStatus, string?)> CheckHealthAsync(CancellationToken ct)
    {
        try
        {
            HealthReport report = await _health.CheckHealthAsync(
                registration => registration.Name == DvrHealthCheck.Name, ct);
            return report.Entries.TryGetValue(DvrHealthCheck.Name, out HealthReportEntry entry)
                ? (entry.Status, entry.Description)
                : (report.Status, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The DVR health check failed; Settings shows it as unhealthy.");
            return (HealthStatus.Unhealthy, "Health check failed.");
        }
    }

    private void OnScheduleChanged() => _throttle.Signal();

    private void OnThrottleFired() => Changed?.Invoke();

    private void OnAuthenticationStateChanged(Task<AuthenticationState> state)
    {
        _authState = state;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _throttle.Fired -= OnThrottleFired;
        _throttle.Dispose();
        _auth.AuthenticationStateChanged -= OnAuthenticationStateChanged;
        _schedule.Changed -= OnScheduleChanged;
    }
}
