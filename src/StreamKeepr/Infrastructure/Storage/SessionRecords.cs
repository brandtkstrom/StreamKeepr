using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Records;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// The session tables of the record store (ADR-0014): <c>Sessions</c> and
/// <c>SessionMetadataChanges</c>. Each call is one short-lived context and one transaction.
/// Failures propagate; <see cref="SessionStore"/> owns the locking and the failure policy.
/// </summary>
internal sealed class SessionRecords(IDbContextFactory<RecordStoreDbContext> contexts, ILogger logger)
{
    public async Task<SessionRecord?> FindAsync(string sessionId, CancellationToken ct)
    {
        await using RecordStoreDbContext db = await contexts.CreateDbContextAsync(ct);
        SessionRow? row = await db.Sessions.AsNoTracking()
            .SingleOrDefaultAsync(session => session.Id == sessionId, ct);
        if (row is null)
            return null;

        List<SessionMetadataChangeRow> metadata = await db.SessionMetadataChanges.AsNoTracking()
            .Where(change => change.SessionId == sessionId)
            .ToListAsync(ct);
        bool hasChat = await db.ChatSessions.AnyAsync(chat => chat.SessionId == sessionId, ct);
        return ToRecord(row, metadata, hasChat);
    }

    /// <summary>
    /// Every session, in no particular order. Three queries regardless of the count. A row
    /// without metadata cannot be projected; it is logged and left out, never deleted.
    /// </summary>
    public async Task<IReadOnlyList<SessionRecord>> AllAsync(CancellationToken ct)
    {
        await using RecordStoreDbContext db = await contexts.CreateDbContextAsync(ct);
        List<SessionRow> rows = await db.Sessions.AsNoTracking().ToListAsync(ct);
        ILookup<string, SessionMetadataChangeRow> metadata =
            (await db.SessionMetadataChanges.AsNoTracking().ToListAsync(ct))
            .ToLookup(change => change.SessionId, StringComparer.Ordinal);
        HashSet<string> withChat = (await db.ChatSessions.AsNoTracking()
                .Select(chat => chat.SessionId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var records = new List<SessionRecord>(rows.Count);
        foreach (SessionRow row in rows)
        {
            if (!metadata.Contains(row.Id))
            {
                logger.LogWarning("Skipping session {SessionId}: its record has no metadata.", row.Id);
                continue;
            }

            records.Add(ToRecord(row, metadata[row.Id], withChat.Contains(row.Id)));
        }

        return records;
    }

    /// <summary>
    /// Inserts a Recording session and its metadata history in one transaction. An
    /// <paramref name="endedAt"/> is kept for the finalize that follows (Mock seeding only).
    /// </summary>
    public async Task CreateAsync(
        Session session,
        IReadOnlyList<MetadataChange> metadata,
        DateTimeOffset? endedAt,
        CancellationToken ct)
    {
        if (metadata.Count == 0)
            throw new ArgumentException("A session starts with at least one metadata entry.", nameof(metadata));

        await using RecordStoreDbContext db = await contexts.CreateDbContextAsync(ct);
        db.Sessions.Add(new SessionRow
        {
            Id = session.Id,
            Channel = session.Channel,
            StreamId = session.StreamId,
            StartedAt = session.StartedAt,
            Status = SessionStatus.Recording,
            EndedAt = endedAt,
        });
        db.SessionMetadataChanges.AddRange(metadata.Select(change => ToRow(session.Id, change)));
        await db.SaveChangesAsync(ct);
    }

    public async Task AppendMetadataAsync(string sessionId, MetadataChange change, CancellationToken ct)
    {
        await using RecordStoreDbContext db = await contexts.CreateDbContextAsync(ct);
        db.SessionMetadataChanges.Add(ToRow(sessionId, change));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Returns a session to Recording and clears its final aggregates.</summary>
    public async Task MarkRecordingAsync(string sessionId, CancellationToken ct)
    {
        await using RecordStoreDbContext db = await contexts.CreateDbContextAsync(ct);
        int updated = await db.Sessions
            .Where(session => session.Id == sessionId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(session => session.Status, SessionStatus.Recording)
                    .SetProperty(session => session.EndedAt, (DateTimeOffset?)null)
                    .SetProperty(session => session.DurationSeconds, (double?)null)
                    .SetProperty(session => session.SizeBytes, (long?)null)
                    .SetProperty(session => session.PartCount, (int?)null)
                    .SetProperty(session => session.SegmentCount, (int?)null),
                ct);
        EnsureUpdated(updated, sessionId);
    }

    /// <summary>Marks a session Complete with the aggregates its finalize computed.</summary>
    public async Task MarkCompleteAsync(SessionRecord complete, CancellationToken ct)
    {
        await using RecordStoreDbContext db = await contexts.CreateDbContextAsync(ct);
        int updated = await db.Sessions
            .Where(session => session.Id == complete.Id)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(session => session.Status, SessionStatus.Complete)
                    .SetProperty(session => session.EndedAt, complete.EndedAt)
                    .SetProperty(session => session.DurationSeconds, complete.DurationSeconds)
                    .SetProperty(session => session.SizeBytes, complete.SizeBytes)
                    .SetProperty(session => session.PartCount, complete.PartCount)
                    .SetProperty(session => session.SegmentCount, complete.SegmentCount),
                ct);
        EnsureUpdated(updated, complete.Id);
    }

    public async Task MarkThumbnailAsync(string sessionId, CancellationToken ct)
    {
        await using RecordStoreDbContext db = await contexts.CreateDbContextAsync(ct);
        int updated = await db.Sessions
            .Where(session => session.Id == sessionId)
            .ExecuteUpdateAsync(set => set.SetProperty(session => session.HasThumbnail, true), ct);
        EnsureUpdated(updated, sessionId);
    }

    /// <summary>Deletes the session row; its metadata and other dependent rows cascade.</summary>
    public async Task DeleteAsync(string sessionId, CancellationToken ct)
    {
        await using RecordStoreDbContext db = await contexts.CreateDbContextAsync(ct);
        await db.Sessions.Where(session => session.Id == sessionId).ExecuteDeleteAsync(ct);
    }

    /// <summary>See <see cref="RecordStoreConnection.CheckpointWriteAheadLog(IDbContextFactory{RecordStoreDbContext})"/>.</summary>
    public bool CheckpointWriteAheadLog() => RecordStoreConnection.CheckpointWriteAheadLog(contexts);

    private static void EnsureUpdated(int updated, string sessionId)
    {
        if (updated == 0)
            throw new InvalidDataException($"Session {sessionId} has no record.");
    }

    private static SessionMetadataChangeRow ToRow(string sessionId, MetadataChange change) => new()
    {
        SessionId = sessionId,
        At = change.At,
        Title = change.Title,
        Game = change.Game,
    };

    private static SessionRecord ToRecord(
        SessionRow row, IEnumerable<SessionMetadataChangeRow> metadata, bool hasChat)
    {
        // Insertion order breaks ties between changes observed at the same instant.
        ImmutableList<MetadataChange> history = metadata
            .OrderBy(change => change.At)
            .ThenBy(change => change.Id)
            .Select(change => new MetadataChange(change.At, change.Title, change.Game))
            .ToImmutableList();
        if (history.IsEmpty)
            throw new InvalidDataException($"Session {row.Id} has no metadata.");

        return new SessionRecord(
            row.Id,
            row.Channel,
            row.StreamId,
            row.StartedAt,
            row.Status,
            history,
            row.EndedAt,
            row.DurationSeconds,
            row.SizeBytes,
            row.PartCount,
            row.SegmentCount,
            row.HasThumbnail,
            hasChat);
    }
}
