using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using StreamKeepr.Infrastructure.Records;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>Rows one chat writer stores in one transaction, with the session's running totals.</summary>
internal sealed record ChatBatch(
    string SessionId,
    Func<ChatSessionRow> NewSession,
    IReadOnlyList<ChatMessageRow> Messages,
    IReadOnlyList<ChatEventRow> Events,
    long Bytes,
    long LastMessageAt,
    bool Truncated);

/// <summary>A position in a session's messages, ordered by <c>At</c> then row id.</summary>
internal readonly record struct ChatCursor(long At, long Id);

/// <summary>
/// The chat tables of the record store (ADR-0014): <c>ChatSessions</c>, <c>ChatMessages</c> and
/// <c>ChatEvents</c>. Each call is one short-lived context. Writes are synchronous because the
/// capture consumer is; reads are asynchronous for the request path. Failures propagate;
/// <see cref="ChatSessionWriter"/> owns the log-and-skip policy.
/// </summary>
internal sealed class ChatRecords(IDbContextFactory<RecordStoreDbContext> contexts)
{
    /// <summary>The badge and emote columns hold the chat API's JSON shape.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Ids per statement when the purge deletes or redacts by row id.</summary>
    private const int PurgeChunk = 500;

    public static string SerializeBadges(IEnumerable<StoredChatBadge> badges) =>
        JsonSerializer.Serialize(badges.ToArray(), Json);

    public static string SerializeEmotes(IEnumerable<StoredChatEmote> emotes) =>
        JsonSerializer.Serialize(emotes.ToArray(), Json);

    public static IReadOnlyList<T> DeserializeList<T>(string json) =>
        JsonSerializer.Deserialize<T[]>(json, Json) ?? [];

    /// <summary>
    /// Stores a batch: creates the session's chat row on its first batch (or returns a reopened
    /// one to recording) and adds the rows and totals, all in one transaction.
    /// </summary>
    public void Write(ChatBatch batch)
    {
        using RecordStoreDbContext db = contexts.CreateDbContext();
        ChatSessionRow? chat = db.ChatSessions.Find(batch.SessionId);
        if (chat is null)
        {
            chat = batch.NewSession();
            db.ChatSessions.Add(chat);
        }
        else if (chat.Status != ChatStates.Recording)
        {
            chat.Status = ChatStates.Recording;
            chat.EndedAt = null;
        }

        chat.MessageCount += batch.Messages.Count;
        chat.StoredBytes += batch.Bytes;
        chat.Truncated |= batch.Truncated;
        if (batch.LastMessageAt > 0 &&
            (chat.LastMessageAt is null || chat.LastMessageAt.Value.ToUnixTimeMilliseconds() < batch.LastMessageAt))
        {
            chat.LastMessageAt = DateTimeOffset.FromUnixTimeMilliseconds(batch.LastMessageAt);
        }

        db.ChatMessages.AddRange(batch.Messages);
        db.ChatEvents.AddRange(batch.Events);
        db.SaveChanges();
    }

    /// <summary>
    /// Marks a session's chat complete with an <c>end</c> event. Returns false, writing nothing,
    /// when the session has no chat.
    /// </summary>
    public bool Finish(string sessionId, DateTimeOffset endedAt)
    {
        using RecordStoreDbContext db = contexts.CreateDbContext();
        ChatSessionRow? chat = db.ChatSessions.Find(sessionId);
        if (chat is null)
            return false;

        db.ChatEvents.Add(NewEvent(sessionId, ChatEventKinds.End, endedAt.ToUnixTimeMilliseconds()));
        chat.Status = ChatStates.Complete;
        chat.EndedAt = endedAt;
        db.SaveChanges();
        return true;
    }

    /// <summary>
    /// The opt-in finalize purge (ADR 0009): deletes removed messages, redacts replies that quote
    /// them, and replaces the tombstones with one <c>purged</c> event, in one transaction so a
    /// failure leaves the log as it was. Returns null when there was nothing to purge.
    /// </summary>
    public (int Removed, int Redacted)? Purge(string sessionId, DateTimeOffset at)
    {
        using RecordStoreDbContext db = contexts.CreateDbContext();
        using var transaction = db.Database.BeginTransaction();

        ChatTombstones tombstones = ChatTombstones.From(
            db.ChatEvents.AsNoTracking().Where(row => row.SessionId == sessionId).ToList());
        if (tombstones.IsEmpty)
            return null;

        // Only the columns the rule reads, so a large log is not loaded whole.
        var keys = db.ChatMessages.AsNoTracking()
            .Where(row => row.SessionId == sessionId)
            .Select(row => new { row.Id, row.MessageId, row.At, row.UserId, row.Login, row.ReplyMessageId, row.ReplyLogin })
            .ToList();

        // Pass 1 finds every removed message, so any reply can be redacted regardless of order.
        var removedIds = new HashSet<string>(StringComparer.Ordinal);
        var removedRows = new HashSet<long>();
        foreach (var key in keys)
        {
            if (tombstones.Removes(key.MessageId, key.At, key.UserId, key.Login))
            {
                removedIds.Add(key.MessageId);
                removedRows.Add(key.Id);
            }
        }

        var kept = keys.Where(key => !removedRows.Contains(key.Id)).ToList();
        long[] redactRows = kept
            .Where(key => tombstones.RedactsReply(key.At, key.ReplyMessageId, key.ReplyLogin, removedIds))
            .Select(key => key.Id)
            .ToArray();

        foreach (long[] chunk in removedRows.Chunk(PurgeChunk))
            db.ChatMessages.Where(row => chunk.Contains(row.Id)).ExecuteDelete();

        foreach (long[] chunk in redactRows.Chunk(PurgeChunk))
        {
            db.ChatMessages.Where(row => chunk.Contains(row.Id)).ExecuteUpdate(set => set
                .SetProperty(row => row.ReplyMessageId, (string?)null)
                .SetProperty(row => row.ReplyLogin, (string?)null)
                .SetProperty(row => row.ReplyDisplay, (string?)null)
                .SetProperty(row => row.ReplyText, (string?)null));
        }

        db.ChatEvents
            .Where(row => row.SessionId == sessionId &&
                          (row.Kind == ChatEventKinds.Delete ||
                           row.Kind == ChatEventKinds.ClearUser ||
                           row.Kind == ChatEventKinds.ClearAll))
            .ExecuteDelete();

        int removed = removedRows.Count;
        ChatEventRow purged = NewEvent(sessionId, ChatEventKinds.Purged, at.ToUnixTimeMilliseconds());
        purged.Count = removed;
        purged.Reason = $"{removed} messages, {redactRows.Length} replies";
        db.ChatEvents.Add(purged);

        ChatSessionRow chat = db.ChatSessions.Single(row => row.SessionId == sessionId);
        chat.MessageCount = kept.Count;
        chat.LastMessageAt = kept.Count > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(kept.Max(key => key.At))
            : null;
        chat.PurgedCount = (chat.PurgedCount ?? 0) + removed;
        chat.PurgedAt = at;
        chat.PurgedDeletedText = true;
        db.SaveChanges();
        transaction.Commit();
        return (removed, redactRows.Length);
    }

    public async Task<bool> HasChatAsync(string sessionId, CancellationToken ct)
    {
        await using RecordStoreDbContext db = await contexts.CreateDbContextAsync(ct);
        return await db.ChatSessions.AnyAsync(row => row.SessionId == sessionId, ct);
    }

    public async Task<ChatSessionRow?> FindSessionAsync(string sessionId, CancellationToken ct)
    {
        await using RecordStoreDbContext db = await contexts.CreateDbContextAsync(ct);
        return await db.ChatSessions.AsNoTracking()
            .SingleOrDefaultAsync(row => row.SessionId == sessionId, ct);
    }

    /// <summary>Every event of a session in the order it was stored.</summary>
    public async Task<IReadOnlyList<ChatEventRow>> EventsAsync(string sessionId, CancellationToken ct)
    {
        await using RecordStoreDbContext db = await contexts.CreateDbContextAsync(ct);
        return await db.ChatEvents.AsNoTracking()
            .Where(row => row.SessionId == sessionId)
            .OrderBy(row => row.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Up to <paramref name="take"/> messages with <c>At</c> in [<paramref name="fromAt"/>,
    /// <paramref name="toAt"/>], oldest or newest first, strictly past <paramref name="after"/>
    /// in that order. Served by the (SessionId, At) index.
    /// </summary>
    public async Task<IReadOnlyList<ChatMessageRow>> MessagesAsync(
        string sessionId,
        long fromAt,
        long toAt,
        bool newestFirst,
        ChatCursor? after,
        int take,
        CancellationToken ct)
    {
        await using RecordStoreDbContext db = await contexts.CreateDbContextAsync(ct);
        IQueryable<ChatMessageRow> rows = db.ChatMessages.AsNoTracking()
            .Where(row => row.SessionId == sessionId && row.At >= fromAt && row.At <= toAt);

        if (after is { } cursor)
        {
            rows = newestFirst
                ? rows.Where(row => row.At < cursor.At || (row.At == cursor.At && row.Id < cursor.Id))
                : rows.Where(row => row.At > cursor.At || (row.At == cursor.At && row.Id > cursor.Id));
        }

        rows = newestFirst
            ? rows.OrderByDescending(row => row.At).ThenByDescending(row => row.Id)
            : rows.OrderBy(row => row.At).ThenBy(row => row.Id);
        return await rows.Take(take).ToListAsync(ct);
    }

    public static ChatEventRow NewEvent(string sessionId, string kind, long at) => new()
    {
        SessionId = sessionId,
        Kind = kind,
        At = at,
    };
}
