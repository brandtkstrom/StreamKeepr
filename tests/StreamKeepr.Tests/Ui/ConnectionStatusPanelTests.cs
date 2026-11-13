using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Components.Features.Settings;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class ConnectionStatusPanelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly BunitContext _context = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly TestConnectionStatusView _view = new();

    public ConnectionStatusPanelTests()
    {
        _context.Services.AddLogging();
        _context.Services.AddSingleton<TimeProvider>(_clock);
        _context.Services.AddSingleton<IConnectionStatusView>(_view);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void Full_coverage_shows_health_transport_connection_coverage_and_liveness()
    {
        _view.Status = Status() with
        {
            LastMessageAt = Now.AddSeconds(-8),
            LastPollAt = Now.AddSeconds(-20),
            LastSuccessfulPollAt = Now.AddSeconds(-20),
            ApiRoundTrip = TimeSpan.FromMilliseconds(180),
            PollInterval = TimeSpan.FromSeconds(300),
        };

        IRenderedComponent<ConnectionStatusPanel> cut = _context.Render<ConnectionStatusPanel>();

        Assert.Equal("Connection status", cut.Find("[data-connection-status] h2").TextContent);
        Assert.Equal("Healthy", Chip(cut, "[data-health] .sk-status"));
        Assert.Contains("3 channels watched.", cut.Find("[data-health]").TextContent);
        Assert.Equal("Conduit", cut.Find("[data-transport]").TextContent);
        Assert.Equal("Connected", Chip(cut, "[data-connection]"));
        Assert.Equal("3 of 3 channels", Chip(cut, "[data-coverage]"));
        Assert.Contains("saved", cut.Find("[data-coverage]").ClassList);
        Assert.Empty(cut.FindAll("[data-coverage-note]"));
        Assert.Equal("8 s ago", cut.Find("[data-last-message]").TextContent);
        Assert.Equal("20 s ago", cut.Find("[data-last-poll]").TextContent.Trim());
        Assert.Equal("180 ms", cut.Find("[data-api-time]").TextContent);
        Assert.Equal("Every 5 min", cut.Find("[data-poll-interval]").TextContent);
    }

    [Fact]
    public void Partial_coverage_is_amber_and_explains_the_shorter_poll()
    {
        _view.Status = Status() with { Covered = 2 };

        IRenderedComponent<ConnectionStatusPanel> cut = _context.Render<ConnectionStatusPanel>();

        Assert.Equal("2 of 3 channels", Chip(cut, "[data-coverage]"));
        Assert.Contains("partial", cut.Find("[data-coverage]").ClassList);
        Assert.Equal(
            "All channels are polled every 30 s until every channel is covered.",
            cut.Find("[data-coverage-note] span:not(.sk-icon)").TextContent.Trim());
    }

    [Theory]
    [InlineData(EventSubConnection.Reconnecting, "Reconnecting", "partial")]
    [InlineData(EventSubConnection.WaitingForAdmin, "Waiting for admin sign-in", "partial")]
    [InlineData(EventSubConnection.Off, "Off", "saved")]
    public void Without_a_session_coverage_is_hidden(
        EventSubConnection connection, string label, string css)
    {
        _view.Status = Status() with { Connection = connection, Covered = 0 };

        IRenderedComponent<ConnectionStatusPanel> cut = _context.Render<ConnectionStatusPanel>();

        Assert.Equal(label, Chip(cut, "[data-connection]"));
        Assert.Contains(css, cut.Find("[data-connection]").ClassList);
        Assert.Empty(cut.FindAll("[data-coverage]"));
        Assert.Empty(cut.FindAll("[data-coverage-note]"));
    }

    [Theory]
    [InlineData(HealthStatus.Degraded, "Degraded", "partial")]
    [InlineData(HealthStatus.Unhealthy, "Unhealthy", "failed")]
    public void Health_uses_amber_for_degraded_and_red_for_unhealthy(
        HealthStatus health, string label, string css)
    {
        _view.Status = Status() with { Health = health, HealthDescription = "The poller has stopped." };

        IRenderedComponent<ConnectionStatusPanel> cut = _context.Render<ConnectionStatusPanel>();

        Assert.Equal(label, Chip(cut, "[data-health] .sk-status"));
        Assert.Contains(css, cut.Find("[data-health] .sk-status").ClassList);
        Assert.Contains("The poller has stopped.", cut.Find("[data-health]").TextContent);
    }

    [Fact]
    public void A_failed_poll_shows_when_the_last_successful_one_was()
    {
        _view.Status = Status() with
        {
            LastPollAt = Now.AddSeconds(-5),
            LastSuccessfulPollAt = Now.AddMinutes(-3),
            LastMessageAt = null,
            ApiRoundTrip = null,
            PollInterval = null,
        };

        IRenderedComponent<ConnectionStatusPanel> cut = _context.Render<ConnectionStatusPanel>();

        Assert.Equal("5 s ago", cut.Find("[data-last-poll] [data-poll-attempt]").TextContent);
        Assert.Equal(
            "last successful 3 min ago",
            cut.Find("[data-last-poll] [data-poll-success]").TextContent);
        Assert.Equal("Never", cut.Find("[data-last-message]").TextContent);
        Assert.Equal("—", cut.Find("[data-api-time]").TextContent);
        Assert.Equal("—", cut.Find("[data-poll-interval]").TextContent);
    }

    [Fact]
    public void Refreshes_every_five_seconds_and_on_changed()
    {
        _view.Status = Status();
        IRenderedComponent<ConnectionStatusPanel> cut = _context.Render<ConnectionStatusPanel>();
        Assert.Equal(1, _view.Reads);

        _view.Status = Status() with { Transport = "WebSocket" };
        _clock.Advance(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => Assert.Equal("WebSocket", cut.Find("[data-transport]").TextContent));

        _view.Status = Status() with { Connection = EventSubConnection.Reconnecting };
        _view.RaiseChanged();
        cut.WaitForAssertion(() => Assert.Equal("Reconnecting", Chip(cut, "[data-connection]")));
    }

    [Fact]
    public void Disposing_stops_the_refresh_timer_and_detaches()
    {
        _view.Status = Status();
        IRenderedComponent<ConnectionStatusPanel> cut = _context.Render<ConnectionStatusPanel>();

        cut.Instance.Dispose();
        _clock.Advance(TimeSpan.FromSeconds(10));
        _view.RaiseChanged();

        Assert.Equal(1, _view.Reads);
        Assert.Equal(0, _view.Subscribers);
    }

    [Fact]
    public void A_signed_out_circuit_renders_nothing()
    {
        _view.Throw = new UnauthorizedAccessException();

        IRenderedComponent<ConnectionStatusPanel> cut = _context.Render<ConnectionStatusPanel>();

        Assert.Empty(cut.FindAll("[data-connection-status]"));
    }

    private static string Chip(IRenderedComponent<ConnectionStatusPanel> cut, string selector)
    {
        var chip = cut.Find(selector);
        return chip.TextContent.Replace(chip.QuerySelector(".sk-icon")?.TextContent ?? "\u0000", "").Trim();
    }

    private static ConnectionStatus Status() => new(
        Transport: "Conduit",
        Connection: EventSubConnection.Connected,
        Covered: 3,
        Watched: 3,
        Health: HealthStatus.Healthy,
        HealthDescription: "3 channels watched.",
        Now: Now,
        LastMessageAt: Now,
        LastPollAt: Now,
        LastSuccessfulPollAt: Now,
        ApiRoundTrip: TimeSpan.FromMilliseconds(1),
        PollInterval: TimeSpan.FromSeconds(300),
        UncoveredPollInterval: TimeSpan.FromSeconds(30));
}

/// <summary>A scripted <see cref="IConnectionStatusView"/> for component tests.</summary>
internal sealed class TestConnectionStatusView : IConnectionStatusView
{
    private Action? _changed;

    public ConnectionStatus? Status { get; set; }

    public Exception? Throw { get; set; }

    public int Reads { get; private set; }

    public int Subscribers => _changed?.GetInvocationList().Length ?? 0;

    public event Action? Changed
    {
        add => _changed += value;
        remove => _changed -= value;
    }

    public Task<ConnectionStatus> GetAsync(CancellationToken ct)
    {
        Reads++;
        if (Throw is not null)
            throw Throw;
        return Task.FromResult(Status ?? throw new InvalidOperationException("No status scripted."));
    }

    public void RaiseChanged() => _changed?.Invoke();

    public void Dispose()
    {
    }
}
