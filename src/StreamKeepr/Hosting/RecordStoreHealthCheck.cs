using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StreamKeepr.Infrastructure.Records;

namespace StreamKeepr.Hosting;

/// <summary>
/// Readiness requires the record store to open and to have no pending migrations. Liveness
/// never depends on it. Contexts open without create, so a probe never recreates a lost file.
/// </summary>
public sealed class RecordStoreHealthCheck(IDbContextFactory<RecordStoreDbContext> contexts) : IHealthCheck
{
    public const string Name = "records";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using RecordStoreDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
            if (!await db.Database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Unhealthy("The record store cannot be opened.");

            string[] pending = [.. await db.Database.GetPendingMigrationsAsync(cancellationToken)];
            return pending.Length == 0
                ? HealthCheckResult.Healthy("The record store is open and migrated.")
                : HealthCheckResult.Unhealthy(
                    $"The record store has {pending.Length} pending migrations.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The record store cannot be read.", ex);
        }
    }
}
