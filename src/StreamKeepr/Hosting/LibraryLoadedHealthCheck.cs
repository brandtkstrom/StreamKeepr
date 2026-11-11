using Microsoft.Extensions.Diagnostics.HealthChecks;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Hosting;

/// <summary>Readiness requires startup recovery to have scanned every recording.</summary>
public sealed class LibraryLoadedHealthCheck(RecordingLibrary library) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(library.IsLoaded
            ? HealthCheckResult.Healthy("The recording library is loaded.")
            : HealthCheckResult.Unhealthy("The recording library has not loaded."));
}
