using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Records;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Hosting;
using StreamKeepr.Tests.Records;

namespace StreamKeepr.Tests.Storage;

public sealed class ProgressStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly TestRecordStore _records = new();
    private readonly FailableRecordContexts _contexts;
    private readonly ListLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggers;
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly RecordingLibrary _library = new(new FakeTimeProvider(), NullLogger<RecordingLibrary>.Instance);

    public ProgressStoreTests()
    {
        // The first start creates the database the tests record sessions into.
        _records.StartAsync().GetAwaiter().GetResult();
        _contexts = new FailableRecordContexts(_records.Contexts, _records.DatabasePath);
        _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));
    }

    [Fact]
    public async Task Set_is_readable_at_once_and_clamped_to_the_duration()
    {
        Add(Finished("r1", durationSeconds: 600));
        using ProgressStore store = await CreateAsync();
        int changes = 0;
        store.Changed += () => changes++;

        await store.SetAsync("r1", 125.5, Ct);
        Assert.Equal(125.5, store.Get("r1"));

        await store.SetAsync("r1", -3, Ct);
        Assert.Equal(0, store.Get("r1"));
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Set_and_clear_are_written_through_to_the_record_store()
    {
        Add(Finished("r1", durationSeconds: 600));
        using ProgressStore store = await CreateAsync();

        await store.SetAsync("r1", 42, Ct);
        Assert.Equal(new Dictionary<string, double> { ["r1"] = 42 }, await StoredAsync());
        ResumePositionRow row = await SingleRowAsync();
        Assert.Equal(Now, row.UpdatedAt);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await store.SetAsync("r1", 100, Ct);
        row = await SingleRowAsync();
        Assert.Equal(100, row.PositionSeconds);
        Assert.Equal(Now.AddMinutes(1), row.UpdatedAt);

        await store.ClearAsync("r1", Ct);
        Assert.Empty(await StoredAsync());
    }

    [Fact]
    public async Task Positions_survive_a_restart()
    {
        Add(Finished("r1", durationSeconds: 600));
        Add(Finished("r2", durationSeconds: 600));
        using (ProgressStore store = await CreateAsync())
        {
            await store.SetAsync("r1", 42, Ct);
            await store.SetAsync("r2", 50, Ct);
            await store.ClearAsync("r2", Ct);
        }

        using ProgressStore restarted = await CreateAsync();

        Assert.Equal(42, restarted.Get("r1"));
        Assert.Null(restarted.Get("r2"));
    }

    [Fact]
    public async Task Load_reads_stored_rows_and_skips_invalid_ones()
    {
        Add(Finished("r1", durationSeconds: 600));
        Add(Finished("r2", durationSeconds: 600));
        Add(Finished("r3", durationSeconds: 600));
        await InsertRowAsync("r1", 12.5);
        await InsertRowAsync("r2", -1);
        await InsertRowAsync("r3", double.PositiveInfinity);

        using ProgressStore store = await CreateAsync();

        Assert.Equal(12.5, store.Get("r1"));
        Assert.Null(store.Get("r2"));
        Assert.Null(store.Get("r3"));
    }

    [Fact]
    public async Task Load_requires_a_prepared_record_store()
    {
        using var store = new ProgressStore(
            _library, _contexts, new RecordStoreState(), _clock, NullLogger<ProgressStore>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync(Ct));
    }

    [Fact]
    public async Task Positions_within_thirty_seconds_of_the_end_clear_the_entry()
    {
        Add(Finished("r1", durationSeconds: 600));
        using ProgressStore store = await CreateAsync();
        await store.SetAsync("r1", 100, Ct);

        await store.SetAsync("r1", 571, Ct);
        Assert.Null(store.Get("r1"));
        Assert.Empty(await StoredAsync());

        await store.SetAsync("r1", 900, Ct);
        Assert.Null(store.Get("r1"));
        Assert.Empty(await StoredAsync());
    }

    [Fact]
    public async Task Unknown_recordings_are_ignored()
    {
        using ProgressStore store = await CreateAsync();
        int changes = 0;
        store.Changed += () => changes++;

        await store.SetAsync("missing", 10, Ct);

        Assert.Null(store.Get("missing"));
        Assert.Equal(0, changes);
        Assert.Empty(await StoredAsync());
    }

    [Fact]
    public async Task Live_recordings_keep_positions_near_and_past_the_known_end()
    {
        // While recording the live edge sits inside the 30 s margin, and the library's
        // duration trails the player's, so neither the margin nor the upper clamp applies.
        Add(Finished("live", durationSeconds: 600, recording: true));
        using ProgressStore store = await CreateAsync();

        await store.SetAsync("live", 590, Ct);
        Assert.Equal(590, store.Get("live"));

        await store.SetAsync("live", 612, Ct);
        Assert.Equal(612, store.Get("live"));
        Assert.Equal(new Dictionary<string, double> { ["live"] = 612 }, await StoredAsync());

        await store.SetAsync("live", -3, Ct);
        Assert.Equal(0, store.Get("live"));
    }

    [Fact]
    public async Task Live_recordings_without_a_known_duration_keep_positions()
    {
        Add(Finished("live", durationSeconds: null, recording: true));
        using ProgressStore store = await CreateAsync();

        await store.SetAsync("live", 10, Ct);

        Assert.Equal(10, store.Get("live"));
    }

    [Fact]
    public async Task A_capture_finishing_drops_positions_within_thirty_seconds_of_its_final_end()
    {
        Add(Finished("near", durationSeconds: 500, recording: true));
        Add(Finished("edge", durationSeconds: 500, recording: true));
        Add(Finished("kept", durationSeconds: 500, recording: true));
        _library.MarkLoaded();
        using ProgressStore store = await CreateAsync();
        await store.SetAsync("near", 590, Ct);
        await store.SetAsync("edge", 570, Ct);
        await store.SetAsync("kept", 569.5, Ct);
        int changes = 0;
        store.Changed += () => changes++;

        _library.Upsert(Finished("near", durationSeconds: 600));
        _library.Upsert(Finished("edge", durationSeconds: 600));
        _library.Upsert(Finished("kept", durationSeconds: 600));

        Assert.Null(store.Get("near"));
        Assert.Null(store.Get("edge"));
        Assert.Equal(569.5, store.Get("kept"));
        Assert.Equal(2, changes);
        await store.FlushAsync(Ct);
        Assert.Equal(new Dictionary<string, double> { ["kept"] = 569.5 }, await StoredAsync());
    }

    [Fact]
    public async Task A_capture_still_recording_keeps_positions_near_its_current_end()
    {
        Add(Finished("live", durationSeconds: 600, recording: true));
        _library.MarkLoaded();
        using ProgressStore store = await CreateAsync();
        await store.SetAsync("live", 590, Ct);

        _library.Upsert(Finished("live", durationSeconds: 610, recording: true));

        Assert.Equal(590, store.Get("live"));
    }

    [Fact]
    public async Task A_stored_position_near_the_end_of_a_session_recovered_as_finished_is_dropped_at_startup()
    {
        Add(Finished("near", durationSeconds: 600));
        Add(Finished("kept", durationSeconds: 600));
        await InsertRowAsync("near", 590);
        await InsertRowAsync("kept", 100);
        _library.MarkLoaded();

        using ProgressStore store = await CreateAsync();

        Assert.Null(store.Get("near"));
        Assert.Equal(100, store.Get("kept"));
        await store.FlushAsync(Ct);
        Assert.Equal(new Dictionary<string, double> { ["kept"] = 100 }, await StoredAsync());
    }

    [Fact]
    public async Task Clear_and_library_removal_drop_the_entry()
    {
        Add(Finished("r1", durationSeconds: 600));
        Add(Finished("r2", durationSeconds: 600));
        _library.MarkLoaded();
        using ProgressStore store = await CreateAsync();
        await store.SetAsync("r1", 50, Ct);
        await store.SetAsync("r2", 60, Ct);

        await store.ClearAsync("r1", Ct);
        _library.Remove("r2");

        Assert.Null(store.Get("r1"));
        Assert.Null(store.Get("r2"));
        await store.FlushAsync(Ct);
        Assert.Empty(await StoredAsync());
    }

    [Fact]
    public async Task Deleting_the_session_record_cascades_its_resume_position()
    {
        Add(Finished("r1", durationSeconds: 600));
        Add(Finished("r2", durationSeconds: 600));
        using ProgressStore store = await CreateAsync();
        await store.SetAsync("r1", 50, Ct);
        await store.SetAsync("r2", 60, Ct);

        await using (RecordStoreDbContext db = _records.Contexts.CreateDbContext())
            await db.Sessions.Where(session => session.Id == "r1").ExecuteDeleteAsync(Ct);

        Assert.Equal(new Dictionary<string, double> { ["r2"] = 60 }, await StoredAsync());
    }

    [Fact]
    public async Task A_failed_write_logs_and_keeps_the_position_in_memory()
    {
        Add(Finished("r1", durationSeconds: 600));
        using ProgressStore store = await CreateAsync();
        await store.SetAsync("r1", 10, Ct);
        _contexts.WritesFailing = true;

        await store.SetAsync("r1", 20, Ct);
        await store.ClearAsync("r1", Ct);
        await store.SetAsync("r1", 30, Ct);

        Assert.Equal(30, store.Get("r1"));
        Assert.Equal(
            3,
            _logs.Entries.Count(entry =>
                entry.Level == LogLevel.Warning
                && entry.Message.Contains("resume position", StringComparison.Ordinal)));

        _contexts.WritesFailing = false;
        Assert.Equal(new Dictionary<string, double> { ["r1"] = 10 }, await StoredAsync());
        await store.SetAsync("r1", 40, Ct);
        Assert.Equal(new Dictionary<string, double> { ["r1"] = 40 }, await StoredAsync());
    }

    [Fact]
    public async Task An_unreadable_record_store_starts_empty_with_a_warning()
    {
        Add(Finished("r1", durationSeconds: 600));
        await InsertRowAsync("r1", 12);
        _contexts.Failing = true;

        using ProgressStore store = await CreateAsync();

        Assert.Null(store.Get("r1"));
        Assert.Contains(
            _logs.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("resume positions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_legacy_progress_file_is_neither_read_nor_written()
    {
        string legacy = Path.Combine(
            AppDataPaths.AppDataDirectory(_records.RecordingsRoot), AppDataPaths.ProgressFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        await File.WriteAllTextAsync(legacy, """{"r1":77}""", Ct);
        Add(Finished("r1", durationSeconds: 600));
        Add(Finished("r2", durationSeconds: 600));

        using ProgressStore store = await CreateAsync();
        Assert.Null(store.Get("r1"));

        await store.SetAsync("r2", 20, Ct);
        await store.FlushAsync(Ct);
        await ((IHostedService)store).StopAsync(Ct);

        Assert.Equal("""{"r1":77}""", await File.ReadAllTextAsync(legacy, Ct));
        Assert.Equal(
            [AppDataPaths.ProgressFileName],
            Directory.GetFiles(Path.GetDirectoryName(legacy)!).Select(Path.GetFileName));
    }

    public void Dispose()
    {
        _loggers.Dispose();
        _logs.Dispose();
        _records.Dispose();
    }

    /// <summary>A process (re)start: the record store's startup step, then a loaded progress store.</summary>
    private async Task<ProgressStore> CreateAsync()
    {
        RecordStoreState state = await _records.StartAsync();
        var store = new ProgressStore(
            _library, _contexts, state, _clock, _loggers.CreateLogger<ProgressStore>());
        await store.LoadAsync(Ct);
        return store;
    }

    /// <summary>Records the session, as the session store would, and adds it to the library.</summary>
    private void Add(RecordingSummary summary)
    {
        using (RecordStoreDbContext db = _records.Contexts.CreateDbContext())
        {
            db.Sessions.Add(new SessionRow
            {
                Id = summary.Id,
                Channel = summary.Channel,
                StreamId = "1",
                StartedAt = summary.StartedAt,
                Status = summary.IsRecording ? SessionStatus.Recording : SessionStatus.Complete,
                EndedAt = summary.EndedAt,
                DurationSeconds = summary.IsRecording ? null : summary.DurationSeconds,
            });
            db.SessionMetadataChanges.Add(new SessionMetadataChangeRow
            {
                SessionId = summary.Id,
                At = summary.StartedAt,
                Title = summary.Title,
                Game = summary.Game,
            });
            db.SaveChanges();
        }

        _library.Upsert(summary);
    }

    private async Task InsertRowAsync(string sessionId, double position)
    {
        await using RecordStoreDbContext db = _records.Contexts.CreateDbContext();
        db.ResumePositions.Add(new ResumePositionRow
        {
            SessionId = sessionId,
            PositionSeconds = position,
            UpdatedAt = Now,
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Dictionary<string, double>> StoredAsync()
    {
        await using RecordStoreDbContext db = _records.Contexts.CreateDbContext();
        return await db.ResumePositions.AsNoTracking()
            .ToDictionaryAsync(row => row.SessionId, row => row.PositionSeconds, Ct);
    }

    private async Task<ResumePositionRow> SingleRowAsync()
    {
        await using RecordStoreDbContext db = _records.Contexts.CreateDbContext();
        return await db.ResumePositions.AsNoTracking().SingleAsync(Ct);
    }

    private static RecordingSummary Finished(
        string id, double? durationSeconds, bool recording = false) =>
        new(
            id,
            "ann",
            "Title",
            "Game",
            Now.AddHours(-2),
            recording ? null : Now.AddHours(-1),
            IsRecording: recording,
            durationSeconds,
            SizeBytes: 1024,
            PartCount: 1,
            SegmentCount: 10,
            HasThumbnail: false,
            Chapters: []);
}
