using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Infrastructure.Telemetry;

namespace StreamKeepr.Tests.Telemetry;

/// <summary>Owns a meter factory and telemetry instance for tests that construct services directly.</summary>
internal sealed class TestTelemetry : IDisposable
{
    private readonly ServiceProvider _services;

    public TestTelemetry()
    {
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        MeterFactory = _services.GetRequiredService<IMeterFactory>();
        Value = new StreamKeeprTelemetry(MeterFactory, enabled: true);
    }

    public IMeterFactory MeterFactory { get; }

    public StreamKeeprTelemetry Value { get; }

    public void Dispose()
    {
        Value.Dispose();
        _services.Dispose();
    }
}
