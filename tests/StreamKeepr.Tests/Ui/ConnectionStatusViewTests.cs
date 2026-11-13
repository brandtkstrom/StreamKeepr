using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class ConnectionStatusViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly PollSchedule _schedule;
    private readonly FakeHealth _health = new();
    private readonly ChannelsViewTests.SwitchableAuth _auth = new();

    public ConnectionStatusViewTests() => _schedule = new PollSchedule(_clock);

    [Fact]
    public async Task Every_member_throws_without_a_signed_in_admin()
    {
        using ConnectionStatusView view = Create(signedIn: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => view.GetAsync(default));
        Assert.Equal(0, _health.Calls);
    }

    [Theory]
    [InlineData(PushState.Connected, EventSubConnection.Connected)]
    [InlineData(PushState.PartialCoverage, EventSubConnection.Connected)]
    [InlineData(PushState.EventSubReconnecting, EventSubConnection.Reconnecting)]
    [InlineData(PushState.NoAdminToken, EventSubConnection.WaitingForAdmin)]
    [InlineData(PushState.PollingOnly, EventSubConnection.Off)]
    public async Task Connection_is_derived_from_the_push_state(PushState state, EventSubConnection expected)
    {
        using ConnectionStatusView view = Create();
        _schedule.SetPushStatus(new PushStatus(state));

        Assert.Equal(expected, (await view.GetAsync(default)).Connection);
    }

    [Fact]
    public async Task Coverage_computed_without_a_socket_is_off_not_connected()
    {
        using ConnectionStatusView view = Create();
        _schedule.SetPushStatus(PushStatus.FromCoverage([], watched: 2, sessionOpen: false));

        Assert.Equal(EventSubConnection.Off, (await view.GetAsync(default)).Connection);
    }

    [Fact]
    public async Task Reports_transport_coverage_health_and_liveness()
    {
        using ConnectionStatusView view = Create(transport: "Conduit");
        _health.Next = new HealthReportEntry(
            HealthStatus.Degraded, "Retrying or Twitch unreachable: ann.", TimeSpan.Zero, null, null);
        _schedule.SetPushStatus(PushStatus.FromCoverage(["ann", "bob"], watched: 3));
        _schedule.EventSubMessageReceived();
        _schedule.Polled(succeeded: true, roundTrip: TimeSpan.FromMilliseconds(180));
        _clock.Advance(TimeSpan.FromSeconds(8));

        ConnectionStatus status = await view.GetAsync(default);

        Assert.Equal("Conduit", status.Transport);
        Assert.Equal(EventSubConnection.Connected, status.Connection);
        Assert.Equal((2, 3), (status.Covered, status.Watched));
        Assert.Equal(HealthStatus.Degraded, status.Health);
        Assert.Equal("Retrying or Twitch unreachable: ann.", status.HealthDescription);
        Assert.Equal(Now.AddSeconds(8), status.Now);
        Assert.Equal(Now, status.LastMessageAt);
        Assert.Equal(Now, status.LastPollAt);
        Assert.Equal(Now, status.LastSuccessfulPollAt);
        Assert.Equal(TimeSpan.FromMilliseconds(180), status.ApiRoundTrip);
        Assert.Null(status.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), status.UncoveredPollInterval);
        Assert.Equal([DvrHealthCheck.Name], _health.Checked);
    }

    [Fact]
    public async Task A_failing_health_check_reports_unhealthy_instead_of_throwing()
    {
        using ConnectionStatusView view = Create();
        _health.Throw = new InvalidOperationException("boom");

        ConnectionStatus status = await view.GetAsync(default);

        Assert.Equal(HealthStatus.Unhealthy, status.Health);
        Assert.Equal("Health check failed.", status.HealthDescription);
    }

    [Fact]
    public void Push_changes_raise_one_throttled_changed_and_auth_changes_raise_at_once()
    {
        using ConnectionStatusView view = Create();
        int changed = 0;
        view.Changed += () => changed++;

        _schedule.SetPushStatus(PushStatus.Reconnecting);
        _schedule.SetPushStatus(PushStatus.FromCoverage(["ann"], 1));
        Assert.Equal(0, changed);
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, changed);

        _auth.Set(signedIn: false);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void Dispose_detaches_from_the_schedule()
    {
        ConnectionStatusView view = Create();
        int changed = 0;
        view.Changed += () => changed++;
        view.Dispose();

        _schedule.SetPushStatus(PushStatus.Reconnecting);
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, changed);
    }

    private ConnectionStatusView Create(bool signedIn = true, string transport = "WebSocket")
    {
        _auth.Set(signedIn);
        return new ConnectionStatusView(
            _schedule,
            new NamedEventSubClient(transport),
            _health,
            Options.Create(new TwitchOptions { PollIntervalSeconds = 30 }),
            _auth,
            _clock,
            NullLogger<ConnectionStatusView>.Instance);
    }

    private sealed class NamedEventSubClient(string transport) : IEventSubClient
    {
        public string Transport => transport;

        public Task RunAsync(StreamObservationRoute route, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeHealth : HealthCheckService
    {
        public HealthReportEntry Next { get; set; } =
            new(HealthStatus.Healthy, "1 channels watched.", TimeSpan.Zero, null, null);

        public Exception? Throw { get; set; }

        public int Calls { get; private set; }

        public List<string> Checked { get; } = [];

        public override Task<HealthReport> CheckHealthAsync(
            Func<HealthCheckRegistration, bool>? predicate, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Throw is not null)
                throw Throw;

            foreach (string name in new[] { DvrHealthCheck.Name, "disk", "library" })
            {
                if (predicate?.Invoke(new HealthCheckRegistration(
                        name, _ => null!, null, null)) == true)
                {
                    Checked.Add(name);
                }
            }

            return Task.FromResult(new HealthReport(
                new Dictionary<string, HealthReportEntry> { [DvrHealthCheck.Name] = Next },
                TimeSpan.Zero));
        }
    }
}
