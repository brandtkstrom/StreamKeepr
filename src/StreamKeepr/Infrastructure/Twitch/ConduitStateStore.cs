using Microsoft.EntityFrameworkCore;
using StreamKeepr.Infrastructure.Records;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// The conduit this instance created, and the client ID that owns it. Neither is a secret.
/// Subscription IDs are not stored: Twitch lists them by conduit.
/// </summary>
public sealed record ConduitState(string ConduitId, string ClientId);

/// <summary>Persists <see cref="ConduitState"/> so a restart reuses the conduit instead of leaking one.</summary>
public interface IConduitStateStore
{
    /// <summary>
    /// Returns null when nothing was saved or the saved state is unreadable. A record store that
    /// cannot be read throws, so a transient failure never forgets a conduit that still exists.
    /// </summary>
    Task<ConduitState?> LoadAsync(CancellationToken ct);

    Task SaveAsync(ConduitState state, CancellationToken ct);

    Task ClearAsync(CancellationToken ct);
}

/// <summary>
/// Stores the conduit state as the record store's single <see cref="EventSubConduitRow"/>
/// (ADR-0014). Callers are serialized by the single EventSub loop and the startup retirement
/// service, so no lock is taken; each call is one short transaction.
/// </summary>
public sealed class ConduitStateStore(
    IDbContextFactory<RecordStoreDbContext> records,
    TimeProvider clock,
    ILogger<ConduitStateStore> logger) : IConduitStateStore
{
    public async Task<ConduitState?> LoadAsync(CancellationToken ct)
    {
        await using RecordStoreDbContext db = await records.CreateDbContextAsync(ct);
        EventSubConduitRow? row = await db.EventSubConduits
            .AsNoTracking()
            .SingleOrDefaultAsync(conduit => conduit.Id == EventSubConduitRow.SingletonId, ct);
        if (row is null)
            return null;

        if (!string.IsNullOrWhiteSpace(row.ConduitId) && !string.IsNullOrWhiteSpace(row.ClientId))
            return new ConduitState(row.ConduitId, row.ClientId);

        // Degrade to "no conduit": a new one is created and saved over this row, and the lost one
        // expires on Twitch's side.
        logger.LogWarning(
            "Ignoring unreadable EventSub conduit state in the record store; a new conduit will be created.");
        return null;
    }

    public async Task SaveAsync(ConduitState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.ConduitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.ClientId);

        await using RecordStoreDbContext db = await records.CreateDbContextAsync(ct);
        EventSubConduitRow? row = await db.EventSubConduits
            .SingleOrDefaultAsync(conduit => conduit.Id == EventSubConduitRow.SingletonId, ct);
        if (row is null)
        {
            row = new EventSubConduitRow { ConduitId = state.ConduitId, ClientId = state.ClientId };
            db.EventSubConduits.Add(row);
        }

        row.ConduitId = state.ConduitId;
        row.ClientId = state.ClientId;
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        await using RecordStoreDbContext db = await records.CreateDbContextAsync(ct);
        await db.EventSubConduits
            .Where(conduit => conduit.Id == EventSubConduitRow.SingletonId)
            .ExecuteDeleteAsync(ct);
    }
}
