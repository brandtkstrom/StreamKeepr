using System.Data.Common;
using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Records;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Hosting;
using StreamKeepr.Tests.Records;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Storage;

public sealed class RecordingAdminTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Delete_requires_an_authenticated_admin()
    {
        using var context = new SessionStoreTestContext();
        RecordingAdmin admin = CreateAdmin(context, out _, out _, out _);
        await SeedCompleteAsync(context);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            admin.DeleteAsync(context.Session.Id, Anonymous, Ct));

        Assert.NotNull(context.Library.Find(context.Session.Id));
        Assert.True(Directory.Exists(context.Directory));
    }

    [Fact]
    public async Task Delete_removes_the_folder_library_entry_and_progress()
    {
        using var context = new SessionStoreTestContext();
        RecordingAdmin admin = CreateAdmin(
            context, out TestStorageMonitor storage, out TestProgress progress, out _);
        await SeedCompleteAsync(context);
        SemaphoreSlim gateBefore = context.Store.LockFor(context.Directory);
        int libraryChanges = 0;
        context.Library.Changed += () => libraryChanges++;
        await progress.SetAsync(context.Session.Id, 1, Ct);
        Assert.Equal(1, progress.Get(context.Session.Id));

        await admin.DeleteAsync(context.Session.Id, Admin, Ct);

        Assert.Null(context.Library.Find(context.Session.Id));
        Assert.False(Directory.Exists(context.Directory));
        Assert.Null(progress.Get(context.Session.Id));
        Assert.Contains(context.Session.Id, progress.Cleared);
        Assert.Equal(1, storage.Refreshes);
        Assert.Equal(1, libraryChanges);
        Assert.NotSame(gateBefore, context.Store.LockFor(context.Directory));
    }

    [Fact]
    public async Task Delete_removes_the_viewer_samples_with_the_session()
    {
        using var context = new SessionStoreTestContext();
        RecordingAdmin admin = CreateAdmin(context, out _, out _, out _);
        await SeedCompleteAsync(context);
        await context.Store.AppendViewerSampleAsync(
            context.Session, SessionStoreTestContext.StartedAt.AddSeconds(30), 9, Ct);
        Assert.Single(await ViewerSamples.ReadAsync(context.RecordStore.Contexts, context.Session.Id, Ct));

        await admin.DeleteAsync(context.Session.Id, Admin, Ct);

        Assert.Empty(await ViewerSamples.ReadAsync(context.RecordStore.Contexts, context.Session.Id, Ct));
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            context.Store.AppendViewerSampleAsync(
                context.Session, SessionStoreTestContext.StartedAt.AddSeconds(90), 9, Ct));
        Assert.False(Directory.Exists(context.Directory));
    }

    [Fact]
    public async Task Delete_removes_the_stored_resume_position_with_the_session()
    {
        using var context = new SessionStoreTestContext();
        await SeedCompleteAsync(context);
        context.Library.MarkLoaded();
        using var progress = new ProgressStore(
            context.Library,
            context.RecordStore.Contexts,
            await context.RecordStore.StartAsync(),
            context.Clock,
            NullLogger<ProgressStore>.Instance);
        await progress.LoadAsync(Ct);
        await progress.SetAsync(context.Session.Id, 1, Ct);
        var admin = new RecordingAdmin(
            context.Library,
            context.Store,
            progress,
            new ExportLeaseRegistry(),
            new TestStorageMonitor(),
            Options.Create(new StorageOptions { RecordingsRoot = context.Root }),
            NullLogger<RecordingAdmin>.Instance);

        await admin.DeleteAsync(context.Session.Id, Admin, Ct);
        await progress.FlushAsync(Ct);

        Assert.Null(progress.Get(context.Session.Id));
        await using RecordStoreDbContext db = context.RecordStore.Contexts.CreateDbContext();
        Assert.Empty(await db.ResumePositions.ToListAsync(Ct));
    }

    [Fact]
    public async Task Delete_leaves_no_trace_of_the_session_in_the_database_files()
    {
        const string title = "Zebra-quartz unrepeatable title";
        using var context = new SessionStoreTestContext();
        RecordingAdmin admin = CreateAdmin(context, out _, out _, out _);
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 60));
        context.WriteSegment(1, 1, 10);
        await context.Store.OpenAsync(context.Session, context.Live with { Title = title }, Ct);
        await context.Store.FinalizeAsync(context.Session, Ct);
        Assert.Contains(title, RecordStoreFiles.Text(context.RecordStore.DatabasePath), StringComparison.Ordinal);

        // Another open connection, as the running app always has, keeps SQLite from emptying
        // the write-ahead log on its own when the delete's connection closes.
        await using var other = new SqliteConnection(RecordStoreConnection.ConnectionString(
            context.RecordStore.DatabasePath, SqliteOpenMode.ReadWrite));
        await other.OpenAsync(Ct);
        await using (SqliteCommand read = other.CreateCommand())
        {
            read.CommandText = "SELECT COUNT(*) FROM Sessions;";
            await read.ExecuteScalarAsync(Ct);
        }

        await admin.DeleteAsync(context.Session.Id, Admin, Ct);

        Assert.DoesNotContain(title, RecordStoreFiles.Text(context.RecordStore.DatabasePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_completes_and_logs_an_orphan_folder_it_cannot_remove()
    {
        // Unix permissions make the folder undeletable; CI runs on Linux.
        if (OperatingSystem.IsWindows())
            return;

        using var context = new SessionStoreTestContext();
        var logs = new ListLoggerProvider();
        RecordingAdmin admin = CreateAdmin(
            context, out TestStorageMonitor storage, out TestProgress progress, out _,
            new LoggerFactory([logs]).CreateLogger<RecordingAdmin>());
        await SeedCompleteAsync(context);
        await progress.SetAsync(context.Session.Id, 1, Ct);
        string channelDirectory = Path.GetDirectoryName(context.Directory)!;
        UnixFileMode mode = File.GetUnixFileMode(channelDirectory);
        File.SetUnixFileMode(channelDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await admin.DeleteAsync(context.Session.Id, Admin, Ct);
        }
        finally
        {
            File.SetUnixFileMode(channelDirectory, mode);
        }

        Assert.True(Directory.Exists(context.Directory));
        Assert.Null(context.Library.Find(context.Session.Id));
        Assert.Null(progress.Get(context.Session.Id));
        Assert.Equal(1, storage.Refreshes);
        CapturedLogEntry orphan = Assert.Single(
            logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(context.Directory, orphan.Message, StringComparison.Ordinal);

        // A restart lists only what the record store holds, so the orphan folder stays hidden.
        var restarted = new RecordingLibrary(context.Clock, NullLogger<RecordingLibrary>.Instance);
        await restarted.LoadAsync(context.Root, context.CreateStore(restarted), Ct);
        Assert.Null(restarted.Find(context.Session.Id));
        Assert.Empty(await context.Store.LoadRecordsAsync(Ct));
    }

    [Fact]
    public async Task A_failed_record_delete_keeps_the_recording_listed_and_intact()
    {
        using var context = new SessionStoreTestContext();
        RecordingAdmin admin = CreateAdmin(
            context, out TestStorageMonitor storage, out TestProgress progress, out _);
        await SeedCompleteAsync(context);
        RecordingSummary listed = context.Library.Find(context.Session.Id)!;
        await progress.SetAsync(context.Session.Id, 1, Ct);
        context.Records.WritesFailing = true;

        await Assert.ThrowsAnyAsync<DbException>(() =>
            admin.DeleteAsync(context.Session.Id, Admin, Ct));

        Assert.Same(listed, context.Library.Find(context.Session.Id));
        Assert.True(Directory.Exists(context.Directory));
        Assert.Equal(1, progress.Get(context.Session.Id));
        Assert.Equal(0, storage.Refreshes);
        context.Records.WritesFailing = false;
        Assert.NotNull(await context.Store.TryLoadAsync(context.Directory, Ct));
    }

    [Fact]
    public async Task A_failed_record_delete_keeps_the_resume_position()
    {
        using var context = new SessionStoreTestContext();
        await SeedCompleteAsync(context);
        context.Library.MarkLoaded();
        using var progress = new ProgressStore(
            context.Library,
            context.RecordStore.Contexts,
            await context.RecordStore.StartAsync(),
            context.Clock,
            NullLogger<ProgressStore>.Instance);
        await progress.LoadAsync(Ct);
        await progress.SetAsync(context.Session.Id, 12, Ct);
        var admin = new RecordingAdmin(
            context.Library,
            context.Store,
            progress,
            new ExportLeaseRegistry(),
            new TestStorageMonitor(),
            Options.Create(new StorageOptions { RecordingsRoot = context.Root }),
            NullLogger<RecordingAdmin>.Instance);
        context.Records.WritesFailing = true;

        await Assert.ThrowsAnyAsync<DbException>(() =>
            admin.DeleteAsync(context.Session.Id, Admin, Ct));
        await progress.FlushAsync(Ct);

        Assert.Equal(12, progress.Get(context.Session.Id));
        await using RecordStoreDbContext db = context.RecordStore.Contexts.CreateDbContext();
        Assert.Equal(12, Assert.Single(await db.ResumePositions.ToListAsync(Ct)).PositionSeconds);
    }

    [Fact]
    public async Task Delete_cascades_every_row_of_the_session()
    {
        using var context = new SessionStoreTestContext();
        RecordingAdmin admin = CreateAdmin(context, out _, out _, out _);
        await SeedCompleteAsync(context);
        RecordingSummary summary = context.Library.Find(context.Session.Id)!;
        await context.Store.AppendViewerSampleAsync(
            context.Session, SessionStoreTestContext.StartedAt.AddSeconds(30), 9, Ct);
        var chat = new ChatTestRecords(context.Root);
        ChatLogStore chatStore = chat.CreateStore(new ChatOptions(), context.Clock);
        chatStore.OpenSession(summary, ChatSources.Anonymous);
        chatStore.Append(summary.Channel, new ChatSourceEvent.MessageEvent(new ChatMessage(
            "m1", summary.Channel, summary.StartedAt.AddSeconds(5).ToUnixTimeMilliseconds(),
            AtIsLocal: false, "42", "alice", "Alice", null, [], [], null, null, Action: false, "hi")));
        chatStore.CloseSession(summary.Id, finalize: true);
        await using (RecordStoreDbContext seeded = context.RecordStore.Contexts.CreateDbContext())
        {
            seeded.ResumePositions.Add(new ResumePositionRow
            {
                SessionId = summary.Id, PositionSeconds = 12, UpdatedAt = context.Clock.GetUtcNow(),
            });
            await seeded.SaveChangesAsync(Ct);
        }

        Assert.All(await CountSessionRowsAsync(context, summary.Id), table => Assert.NotEqual(0, table.Value));

        await admin.DeleteAsync(context.Session.Id, Admin, Ct);

        Assert.All(await CountSessionRowsAsync(context, summary.Id), table => Assert.Equal(0, table.Value));
    }

    [Fact]
    public async Task Delete_keeps_finished_export_files()
    {
        using var context = new SessionStoreTestContext();
        RecordingAdmin admin = CreateAdmin(context, out _, out _, out _);
        await SeedCompleteAsync(context);
        string exports = AppDataPaths.ExportsDirectory(context.Root);
        Directory.CreateDirectory(exports);
        string export = Path.Combine(exports, "export-1.mp4");
        await File.WriteAllTextAsync(export, "mp4", Ct);

        await admin.DeleteAsync(context.Session.Id, Admin, Ct);

        Assert.False(Directory.Exists(context.Directory));
        Assert.Equal("mp4", await File.ReadAllTextAsync(export, Ct));
    }

    [Fact]
    public async Task Delete_refuses_a_recording_that_is_still_being_captured()
    {
        using var context = new SessionStoreTestContext();
        RecordingAdmin admin = CreateAdmin(context, out _, out _, out _);
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        context.WriteSegment(1, 1, 10);
        await context.Store.OpenAsync(context.Session, context.Live, Ct);
        Assert.NotNull(context.Library.Find(context.Session.Id));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            admin.DeleteAsync(context.Session.Id, Admin, Ct));

        Assert.True(Directory.Exists(context.Directory));
    }

    [Fact]
    public async Task Delete_waits_for_the_session_lock()
    {
        using var context = new SessionStoreTestContext();
        RecordingAdmin admin = CreateAdmin(context, out _, out _, out _);
        await SeedCompleteAsync(context);
        SemaphoreSlim gate = context.Store.LockFor(context.Directory);

        await gate.WaitAsync();
        Task delete;
        try
        {
            delete = admin.DeleteAsync(context.Session.Id, Admin, Ct);
            Assert.False(delete.IsCompleted);
        }
        finally
        {
            gate.Release();
        }

        await delete.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(context.Library.Find(context.Session.Id));
    }

    [Fact]
    public async Task Delete_refuses_while_an_export_lease_is_held()
    {
        using var context = new SessionStoreTestContext();
        RecordingAdmin admin = CreateAdmin(
            context, out _, out _, out ExportLeaseRegistry leases);
        await SeedCompleteAsync(context);
        using (leases.Acquire(context.Session.Id))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                admin.DeleteAsync(context.Session.Id, Admin, Ct));
            Assert.True(Directory.Exists(context.Directory));
        }
    }

    [Fact]
    public async Task Delete_reports_an_unknown_recording()
    {
        using var context = new SessionStoreTestContext();
        RecordingAdmin admin = CreateAdmin(context, out _, out _, out _);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            admin.DeleteAsync("20260101T000000Z_1", Admin, Ct));
    }

    [Fact]
    public void Lease_registry_makes_delete_and_export_start_mutually_exclusive()
    {
        var leases = new ExportLeaseRegistry();

        IDisposable? held = leases.TryBeginDelete("recording");
        Assert.NotNull(held);
        using (held)
            Assert.Throws<InvalidOperationException>(() => leases.Acquire("recording"));

        using (leases.Acquire("recording"))
            Assert.Null(leases.TryBeginDelete("recording"));
    }

    private static RecordingAdmin CreateAdmin(
        SessionStoreTestContext context,
        out TestStorageMonitor storage,
        out TestProgress progress,
        out ExportLeaseRegistry leases,
        ILogger<RecordingAdmin>? logger = null)
    {
        storage = new TestStorageMonitor();
        progress = new TestProgress();
        leases = new ExportLeaseRegistry();
        var options = Options.Create(new StorageOptions { RecordingsRoot = context.Root });
        return new RecordingAdmin(
            context.Library,
            context.Store,
            progress,
            leases,
            storage,
            options,
            logger ?? NullLogger<RecordingAdmin>.Instance);
    }

    private static async Task SeedCompleteAsync(SessionStoreTestContext context)
    {
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 60));
        context.WriteSegment(1, 1, 10);
        await context.Store.OpenAsync(context.Session, context.Live, Ct);
        context.Clock.Advance(TimeSpan.FromMinutes(1));
        await context.Store.FinalizeAsync(context.Session, Ct);
        Assert.NotNull(context.Library.Find(context.Session.Id));
    }

    /// <summary>The session's row count in every table a delete must clear, by table.</summary>
    private static async Task<Dictionary<string, int>> CountSessionRowsAsync(
        SessionStoreTestContext context, string id)
    {
        await using RecordStoreDbContext db = context.RecordStore.Contexts.CreateDbContext();
        return new Dictionary<string, int>
        {
            ["Sessions"] = await db.Sessions.CountAsync(row => row.Id == id, Ct),
            ["SessionMetadataChanges"] = await db.SessionMetadataChanges.CountAsync(row => row.SessionId == id, Ct),
            ["ChatSessions"] = await db.ChatSessions.CountAsync(row => row.SessionId == id, Ct),
            ["ChatMessages"] = await db.ChatMessages.CountAsync(row => row.SessionId == id, Ct),
            ["ViewerSamples"] = await db.ViewerSamples.CountAsync(row => row.SessionId == id, Ct),
            ["ResumePositions"] = await db.ResumePositions.CountAsync(row => row.SessionId == id, Ct),
        };
    }

    private static ClaimsPrincipal Admin =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"));

    private static ClaimsPrincipal Anonymous => new(new ClaimsIdentity());

    private sealed class TestProgress : IProgressStore
    {
        private readonly Dictionary<string, double> _positions = new(StringComparer.Ordinal);

        public List<string> Cleared { get; } = [];

        public event Action? Changed;

        public double? Get(string recordingId) =>
            _positions.TryGetValue(recordingId, out double position) ? position : null;

        public Task SetAsync(string recordingId, double positionSec, CancellationToken ct)
        {
            _positions[recordingId] = positionSec;
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public Task ClearAsync(string recordingId, CancellationToken ct)
        {
            _positions.Remove(recordingId);
            Cleared.Add(recordingId);
            Changed?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class TestStorageMonitor : IStorageMonitor
    {
        public int Refreshes { get; private set; }

        public StorageView Current => new(0, 0);

        public event Action? Changed;

        public void Refresh()
        {
            Refreshes++;
            Changed?.Invoke();
        }
    }
}
