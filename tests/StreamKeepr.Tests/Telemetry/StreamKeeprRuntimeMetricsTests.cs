using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Telemetry;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Telemetry;

public sealed class StreamKeeprRuntimeMetricsTests
{
    [Fact]
    public void Dvr_channel_gauge_reports_every_state_including_zero()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        var status = new DvrStatus();
        var live = new LiveStream("1", "Title", "Game", clock.GetUtcNow(), null);
        var session = new Session("20261118T120000Z_1", "recording", "1", clock.GetUtcNow(), "/tmp/session");

        status.Applied("idle", null, new DvrState.Idle(), clock.GetUtcNow());
        status.Applied(
            "recording",
            null,
            new DvrState.Recording(
                session, new JobId(session.Id, 1), live, Attempt: 0, HasMedia: false, Streak: 0),
            clock.GetUtcNow());

        var schedule = new PollSchedule(clock);
        _ = new StreamKeeprRuntimeMetrics(
            factory, status, schedule, new FakeStorage(new StorageView(1234, 9999)),
            new FakeEventSub("WebSocket"), clock);

        using var channels = new MetricCollector<int>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.dvr.channels");
        channels.RecordObservableInstruments();

        IReadOnlyList<CollectedMeasurement<int>> snapshot = channels.GetMeasurementSnapshot();
        Assert.Equal(5, snapshot.Count);
        Assert.Contains(snapshot, m => State(m) == "idle" && m.Value == 1);
        Assert.Contains(snapshot, m => State(m) == "recording" && m.Value == 1);
        Assert.Contains(snapshot, m => State(m) == "waiting_to_retry" && m.Value == 0);
        Assert.Contains(snapshot, m => State(m) == "cooling_down" && m.Value == 0);
        Assert.Contains(snapshot, m => State(m) == "paused" && m.Value == 0);
    }

    [Fact]
    public void Poll_coverage_event_sub_and_storage_gauges_read_cached_state()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        var status = new DvrStatus();
        var schedule = new PollSchedule(clock);

        schedule.Polled(succeeded: true);
        clock.Advance(TimeSpan.FromSeconds(30));
        schedule.SetPushStatus(PushStatus.FromCoverage(["streamer"], watched: 2));

        _ = new StreamKeeprRuntimeMetrics(
            factory, status, schedule, new FakeStorage(new StorageView(4242, 9999)),
            new FakeEventSub("WebSocket"), clock);

        using var age = new MetricCollector<double>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.twitch.poll.age");
        using var coverage = new MetricCollector<double>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.twitch.push.coverage");
        using var connected = new MetricCollector<int>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.eventsub.connected");
        using var free = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.storage.free");

        age.RecordObservableInstruments();
        coverage.RecordObservableInstruments();
        connected.RecordObservableInstruments();
        free.RecordObservableInstruments();

        Assert.Equal(30, Assert.Single(age.GetMeasurementSnapshot()).Value);
        Assert.Equal(0.5, Assert.Single(coverage.GetMeasurementSnapshot()).Value);

        CollectedMeasurement<int> connection = Assert.Single(connected.GetMeasurementSnapshot());
        Assert.Equal(1, connection.Value);
        Assert.Equal("WebSocket", connection.Tags["eventsub.transport"]);

        Assert.Equal(4242, Assert.Single(free.GetMeasurementSnapshot()).Value);
    }

    private static string State(CollectedMeasurement<int> measurement) =>
        (string)measurement.Tags["dvr.state"]!;

    private sealed class FakeStorage(StorageView current) : IStorageMonitor
    {
        public StorageView Current { get; } = current;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public void Refresh() { }
    }

    private sealed class FakeEventSub(string transport) : IEventSubClient
    {
        public string Transport => transport;

        public Task RunAsync(StreamObservationRoute route, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
