using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Records;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Records;

namespace StreamKeepr.Tests.Storage;

/// <summary>
/// A started record store for chat tests, kept inside the caller's recordings root so the
/// caller's cleanup removes it. Chat rows reference their session, so <see cref="AddSession"/>
/// records one first, as the session store does before chat capture opens.
/// </summary>
internal sealed class ChatTestRecords
{
    private readonly TestRecordStore _records;

    public ChatTestRecords(string recordingsRoot)
    {
        RecordingsRoot = recordingsRoot;
        _records = new TestRecordStore(recordingsRoot);
        _records.StartAsync().GetAwaiter().GetResult();
    }

    public string RecordingsRoot { get; }

    public IDbContextFactory<RecordStoreDbContext> Contexts => _records.Contexts;

    public string DatabasePath => _records.DatabasePath;

    /// <summary>A second process start against the same database file.</summary>
    public ChatTestRecords Restart() => new(RecordingsRoot);

    public void AddSession(RecordingSummary summary)
    {
        using RecordStoreDbContext db = Contexts.CreateDbContext();
        db.Sessions.Add(new SessionRow
        {
            Id = summary.Id,
            Channel = summary.Channel,
            StreamId = "1",
            StartedAt = summary.StartedAt,
            Status = summary.IsRecording ? SessionStatus.Recording : SessionStatus.Complete,
        });
        db.SaveChanges();
    }

    public ChatLogStore CreateStore(
        ChatOptions options,
        TimeProvider clock,
        ILogger<ChatLogStore>? logger = null,
        IDbContextFactory<RecordStoreDbContext>? contexts = null) =>
        new(
            Options.Create(new StorageOptions { RecordingsRoot = RecordingsRoot }),
            Options.Create(options),
            contexts ?? Contexts,
            clock,
            logger ?? NullLogger<ChatLogStore>.Instance);

    public static ChatReader CreateReader(ChatLogStore store, ChatOptions options) =>
        new(store, Options.Create(options), NullLogger<ChatReader>.Instance);

    public ChatSessionRow? ChatSession(string sessionId)
    {
        using RecordStoreDbContext db = Contexts.CreateDbContext();
        return db.ChatSessions.AsNoTracking().SingleOrDefault(row => row.SessionId == sessionId);
    }

    public List<ChatMessageRow> Messages(string sessionId)
    {
        using RecordStoreDbContext db = Contexts.CreateDbContext();
        return db.ChatMessages.AsNoTracking()
            .Where(row => row.SessionId == sessionId)
            .OrderBy(row => row.Id)
            .ToList();
    }

    public List<ChatEventRow> Events(string sessionId)
    {
        using RecordStoreDbContext db = Contexts.CreateDbContext();
        return db.ChatEvents.AsNoTracking()
            .Where(row => row.SessionId == sessionId)
            .OrderBy(row => row.Id)
            .ToList();
    }
}
