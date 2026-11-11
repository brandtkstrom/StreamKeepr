using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Hosting;

/// <summary>Readiness for the recordings volume: below the minimum is unhealthy, below twice it degrades.</summary>
public sealed class DiskSpaceHealthCheck(
    DiskGuard disk, IOptions<StorageOptions> storage) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        int minFreeGb = storage.Value.MinFreeGb;
        long free = disk.FreeBytes;
        string text = $"{free / DiskGuard.BytesPerGib} GB free";

        HealthCheckResult result =
            !DiskGuard.HasRoom(free, minFreeGb) ? HealthCheckResult.Unhealthy(
                $"{text}; recording paused below {minFreeGb} GB.") :
            !DiskGuard.HasRoom(free, minFreeGb * 2) ? HealthCheckResult.Degraded(text) :
            HealthCheckResult.Healthy(text);

        return Task.FromResult(result);
    }
}
