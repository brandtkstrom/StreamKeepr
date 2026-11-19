using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using StreamKeepr.Infrastructure.Telemetry;

namespace StreamKeepr.Tests.Telemetry;

public sealed class StreamKeeprTelemetryTests
{
    [Fact]
    public void Part_exit_records_kind_duration_and_size()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        using var telemetry = new StreamKeeprTelemetry(factory);
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
    public void Stall_and_force_kill_are_counted()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        using var telemetry = new StreamKeeprTelemetry(factory);
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
        using var telemetry = new StreamKeeprTelemetry(factory);
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
        using var telemetry = new StreamKeeprTelemetry(factory);
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
        using var telemetry = new StreamKeeprTelemetry(factory);
        using var reconnects = new MetricCollector<long>(
            factory, StreamKeeprTelemetry.MeterName, "streamkeepr.eventsub.reconnects");

        telemetry.EventSubReconnectAttempt("WebSocket");

        CollectedMeasurement<long> reconnect = Assert.Single(reconnects.GetMeasurementSnapshot());
        Assert.Equal(1, reconnect.Value);
        Assert.Equal("WebSocket", reconnect.Tags["eventsub.transport"]);
    }
}
