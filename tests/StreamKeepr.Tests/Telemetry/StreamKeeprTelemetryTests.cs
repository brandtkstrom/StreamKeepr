using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using StreamKeepr.Infrastructure.Telemetry;

namespace StreamKeepr.Tests.Telemetry;

public sealed class StreamKeeprTelemetryTests
{
    [Fact]
    public void Disabled_telemetry_records_nothing()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        using var telemetry = new StreamKeeprTelemetry(factory, enabled: false);
        using var parts = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.parts");
        using var drops = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.chat.dropped");

        telemetry.PartExited(StreamKeepr.Domain.RecorderExitKind.Killed, TimeSpan.FromSeconds(1), 2);
        telemetry.ChatDropped(3);

        Assert.Empty(parts.GetMeasurementSnapshot());
        Assert.Empty(drops.GetMeasurementSnapshot());
    }

    [Fact]
    public void Part_exit_records_kind_duration_and_size()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        using var telemetry = new StreamKeeprTelemetry(factory, enabled: true);
        using var parts = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.parts");
        using var durations = new MetricCollector<double>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.part.duration");
        using var sizes = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.part.size");

        telemetry.PartExited(StreamKeepr.Domain.RecorderExitKind.Killed, TimeSpan.FromSeconds(42), 1234);

        CollectedMeasurement<long> part = Assert.Single(parts.GetMeasurementSnapshot());
        Assert.Equal(1, part.Value);
        Assert.Equal("killed", part.Tags["recorder.exit_kind"]);

        CollectedMeasurement<double> duration = Assert.Single(durations.GetMeasurementSnapshot());
        Assert.Equal(42, duration.Value);
        Assert.Equal("killed", duration.Tags["recorder.exit_kind"]);

        CollectedMeasurement<long> size = Assert.Single(sizes.GetMeasurementSnapshot());
        Assert.Equal(1234, size.Value);
        Assert.Equal("killed", size.Tags["recorder.exit_kind"]);
    }

    [Fact]
    public void An_unknown_part_size_counts_the_part_but_records_no_size()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        using var telemetry = new StreamKeeprTelemetry(factory, enabled: true);
        using var parts = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.parts");
        using var sizes = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.part.size");

        telemetry.PartExited(StreamKeepr.Domain.RecorderExitKind.Exited, TimeSpan.FromSeconds(5), bytes: null);

        Assert.Single(parts.GetMeasurementSnapshot());
        Assert.Empty(sizes.GetMeasurementSnapshot());
    }

    [Fact]
    public void Stall_and_force_kill_are_counted()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        using var telemetry = new StreamKeeprTelemetry(factory, enabled: true);
        using var stalls = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.stalls");
        using var kills = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.force_kills");

        telemetry.Stall();
        telemetry.ForceKill();

        Assert.Equal(1, Assert.Single(stalls.GetMeasurementSnapshot()).Value);
        Assert.Equal(1, Assert.Single(kills.GetMeasurementSnapshot()).Value);
    }

    [Fact]
    public void Chat_drops_record_the_dropped_count()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        using var telemetry = new StreamKeeprTelemetry(factory, enabled: true);
        using var dropped = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.chat.dropped");

        telemetry.ChatDropped(7);

        Assert.Equal(7, Assert.Single(dropped.GetMeasurementSnapshot()).Value);
    }

    [Fact]
    public void Export_outcome_is_counted()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        using var telemetry = new StreamKeeprTelemetry(factory, enabled: true);
        using var jobs = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.export.jobs");

        telemetry.ExportFinished("failed");

        CollectedMeasurement<long> job = Assert.Single(jobs.GetMeasurementSnapshot());
        Assert.Equal(1, job.Value);
        Assert.Equal("failed", job.Tags["export.outcome"]);
    }

    [Fact]
    public void EventSub_reconnect_records_the_transport()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        using var telemetry = new StreamKeeprTelemetry(factory, enabled: true);
        using var reconnects = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.eventsub.reconnects");

        telemetry.EventSubReconnectAttempt("WebSocket");

        CollectedMeasurement<long> reconnect = Assert.Single(reconnects.GetMeasurementSnapshot());
        Assert.Equal(1, reconnect.Value);
        Assert.Equal("WebSocket", reconnect.Tags["eventsub.transport"]);
    }

    [Fact]
    public void Initialize_counters_publishes_a_zero_for_every_known_series()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        using var telemetry = new StreamKeeprTelemetry(factory, enabled: true);
        using var parts = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.parts");
        using var stalls = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.stalls");
        using var kills = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.force_kills");
        using var dropped = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.chat.dropped");
        using var jobs = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.export.jobs");
        using var reconnects = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.eventsub.reconnects");

        telemetry.InitializeCounters("Conduit");

        Assert.All(
            new[] { parts, stalls, kills, dropped, jobs, reconnects }.SelectMany(
                collector => collector.GetMeasurementSnapshot()),
            measurement => Assert.Equal(0, measurement.Value));
        Assert.Equal(
            ["exited", "killed", "stopped"],
            parts.GetMeasurementSnapshot()
                .Select(measurement => (string)measurement.Tags["recorder.exit_kind"]!)
                .Order(StringComparer.Ordinal));
        Assert.Single(stalls.GetMeasurementSnapshot());
        Assert.Single(kills.GetMeasurementSnapshot());
        Assert.Single(dropped.GetMeasurementSnapshot());
        Assert.Equal(
            ["cancelled", "failed", "succeeded"],
            jobs.GetMeasurementSnapshot()
                .Select(measurement => (string)measurement.Tags["export.outcome"]!)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            "Conduit",
            Assert.Single(reconnects.GetMeasurementSnapshot()).Tags["eventsub.transport"]);
    }

    [Fact]
    public void Initialize_counters_publishes_nothing_when_disabled()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        using var telemetry = new StreamKeeprTelemetry(factory, enabled: false);
        using var parts = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.recorder.parts");

        telemetry.InitializeCounters("WebSocket");

        Assert.Empty(parts.GetMeasurementSnapshot());
    }
}
