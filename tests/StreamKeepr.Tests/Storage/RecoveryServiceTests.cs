using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Storage;

public sealed class RecoveryServiceTests : IDisposable
{
    private const string Channel = "streamer";

    private static readonly ChannelEntry[] Watched = [new(Channel)];

    private readonly SessionStoreTestContext _context = new();
    private readonly RecoveryService _recovery;

    public RecoveryServiceTests()
    {
        _recovery = CreateRecovery(NullLoggerFactory.Instance);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task Newest_unfinished_session_resumes_and_older_sessions_are_finalized()
    {
        Session older = CreateSession("111", SessionStoreTestContext.StartedAt);
        Session newer = CreateSession("222", SessionStoreTestContext.StartedAt.AddHours(1));
        await WriteRecordingAsync(older, "111");
        await WriteRecordingAsync(newer, "222");

        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await _recovery.RecoverAsync(Watched, CancellationToken.None);

        RecoveredChannel channel = recovered[Channel];
        Assert.NotNull(channel.Unfinished);
        Assert.Equal(newer.Id, channel.Unfinished.Session.Id);
        Assert.Equal(2, channel.Unfinished.NextPart);
        Assert.False(channel.Unfinished.Complete);

        Assert.NotNull(channel.Finished);
        Assert.Equal(older.Id, channel.Finished.Session.Id);
        Assert.True(channel.Finished.Complete);

        SessionRecord? olderManifest = await _context.Store.TryLoadAsync(
            older.Directory, CancellationToken.None);
        Assert.Equal(SessionStatus.Complete, olderManifest!.Status);
        Assert.True(File.Exists(Path.Combine(older.Directory, SessionStore.IndexFileName)));

        SessionRecord? newerManifest = await _context.Store.TryLoadAsync(
            newer.Directory, CancellationToken.None);
        Assert.Equal(SessionStatus.Recording, newerManifest!.Status);

        Assert.True(_context.Library.IsLoaded);
        Assert.False(_context.Library.Find(older.Id)!.IsRecording);
        Assert.True(_context.Library.Find(newer.Id)!.IsRecording);
    }

    [Fact]
    public async Task Complete_session_without_an_index_is_re_finalized()
    {
        Session session = CreateSession("333", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "333");
        await _context.Store.FinalizeAsync(session, CancellationToken.None);
        File.Delete(Path.Combine(session.Directory, SessionStore.IndexFileName));

        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await _recovery.RecoverAsync(Watched, CancellationToken.None);

        RecoveredChannel channel = recovered[Channel];
        Assert.Null(channel.Unfinished);
        Assert.NotNull(channel.Finished);
        Assert.Equal(session.Id, channel.Finished.Session.Id);
        Assert.True(channel.Finished.Complete);
        Assert.True(File.Exists(Path.Combine(session.Directory, SessionStore.IndexFileName)));

        SessionRecord? manifest = await _context.Store.TryLoadAsync(
            session.Directory, CancellationToken.None);
        Assert.Equal(SessionStatus.Complete, manifest!.Status);
        Assert.True(_context.Library.IsLoaded);
    }

    [Fact]
    public async Task Recording_session_cleans_temp_files_and_orphans()
    {
        Session session = CreateSession("444", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "444");
        string orphanSegment = Path.Combine(session.Directory, "p001_00001.m4s");
        string orphanInit = Path.Combine(session.Directory, "init_999.mp4");
        string temp = Path.Combine(session.Directory, "session.json.tmp");
        await File.WriteAllBytesAsync(orphanSegment, [9, 9]);
        await File.WriteAllBytesAsync(orphanInit, [9, 9]);
        await File.WriteAllTextAsync(temp, "stale");

        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await _recovery.RecoverAsync(Watched, CancellationToken.None);

        RecoveredChannel channel = recovered[Channel];
        Assert.NotNull(channel.Unfinished);
        Assert.Equal(2, channel.Unfinished.NextPart);
        Assert.False(File.Exists(orphanSegment));
        Assert.False(File.Exists(orphanInit));
        Assert.False(File.Exists(temp));
        Assert.True(File.Exists(Path.Combine(session.Directory, "p001_00000.m4s")));
        Assert.True(File.Exists(Path.Combine(session.Directory, "init_001.mp4")));
    }

    [Fact]
    public async Task Folders_without_a_record_are_ignored_and_unparseable_parts_are_skipped()
    {
        // A pre-upgrade recording: its manifest is never read (ADR-0014 clean break).
        Session legacy = CreateSession("555", SessionStoreTestContext.StartedAt);
        Directory.CreateDirectory(legacy.Directory);
        WritePart(legacy, validPart: true);
        await File.WriteAllTextAsync(Path.Combine(legacy.Directory, "session.json"), "{}");
        string legacyTemp = Path.Combine(legacy.Directory, "stale.tmp");
        await File.WriteAllTextAsync(legacyTemp, "stale");

        Session unknown = CreateSession("556", SessionStoreTestContext.StartedAt.AddMinutes(1));
        Directory.CreateDirectory(unknown.Directory);
        string unknownMedia = Path.Combine(unknown.Directory, "keep.m4s");
        await File.WriteAllBytesAsync(unknownMedia, [1, 2, 3]);

        Session badPart = CreateSession("557", SessionStoreTestContext.StartedAt.AddMinutes(2));
        await WriteRecordingAsync(badPart, "557", validPart: false);
        string badTemp = Path.Combine(badPart.Directory, "stale.tmp");
        await File.WriteAllTextAsync(badTemp, "stale");

        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await _recovery.RecoverAsync(Watched, CancellationToken.None);

        Assert.Null(recovered[Channel].Unfinished);
        Assert.Null(recovered[Channel].Finished);
        Assert.True(File.Exists(legacyTemp), "an unrecorded folder is never touched");
        Assert.True(File.Exists(unknownMedia));
        Assert.Null(_context.Library.Find(legacy.Id));
        Assert.False(File.Exists(badTemp));
        Assert.True(File.Exists(Path.Combine(badPart.Directory, "p001_00000.m4s")));
        Assert.NotNull(await _context.Store.TryLoadAsync(badPart.Directory, CancellationToken.None));
        Assert.True(_context.Library.IsLoaded);
    }

    [Fact]
    public async Task A_record_whose_folder_is_missing_is_skipped_and_kept()
    {
        Session session = CreateSession("558", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "558");
        await _context.Store.FinalizeAsync(session, CancellationToken.None);
        Directory.Delete(session.Directory, recursive: true);

        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await _recovery.RecoverAsync(Watched, CancellationToken.None);

        Assert.Null(recovered[Channel].Finished);
        Assert.Null(_context.Library.Find(session.Id));
        Assert.False(Directory.Exists(session.Directory));
        Assert.NotNull(await _context.Store.TryLoadAsync(session.Directory, CancellationToken.None));
    }

    [Fact]
    public async Task A_restart_loads_the_library_from_the_record_store()
    {
        Session complete = CreateSession("560", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(complete, "560");
        // A dated part, so the title change lands on the timeline as a chapter at 0.
        await File.WriteAllTextAsync(
            Path.Combine(complete.Directory, "p001.m3u8"),
            PlaylistTestData.Playlist(
                6, "init_001.mp4", PlaylistTestData.Segment("p001_00000.m4s", "2026-11-10T16:35:00Z", 6)));
        await _context.Store.AppendMetadataAsync(
            complete,
            new LiveStream("560", "Second title", "Second game", complete.StartedAt, null),
            CancellationToken.None);
        await _context.Store.FinalizeAsync(complete, CancellationToken.None);
        await _context.Store.SaveThumbnailAsync(
            complete.Directory, await WriteImageAsync(complete), CancellationToken.None);
        RecordingSummary before = _context.Library.Find(complete.Id)!;

        Session live = CreateSession("561", SessionStoreTestContext.StartedAt.AddHours(1));
        await WriteRecordingAsync(live, "561");

        (RecoveryService recovery, RecordingLibrary library) = Restart();
        Assert.False(library.IsLoaded);

        await recovery.RecoverAsync(Watched, CancellationToken.None);

        Assert.True(library.IsLoaded);
        Assert.Equal([live.Id, complete.Id], library.All.Select(summary => summary.Id));

        RecordingSummary reloaded = library.Find(complete.Id)!;
        Assert.Equal("Second title", reloaded.Title);
        Assert.Equal("Second game", reloaded.Game);
        Assert.False(reloaded.IsRecording);
        Assert.Equal(before.EndedAt, reloaded.EndedAt);
        Assert.Equal(6.0, reloaded.DurationSeconds);
        Assert.Equal(before.SizeBytes, reloaded.SizeBytes);
        Assert.Equal(1, reloaded.PartCount);
        Assert.Equal(1, reloaded.SegmentCount);
        Assert.True(reloaded.HasThumbnail);
        Assert.Equal(before.Chapters, reloaded.Chapters);
        Assert.Equal("Second title", Assert.Single(reloaded.Chapters).Title);

        RecordingSummary recording = library.Find(live.Id)!;
        Assert.True(recording.IsRecording);
        Assert.Equal(1, recording.PartCount);
        Assert.Equal(1, recording.SegmentCount);
        Assert.Null(recording.DurationSeconds);
        Assert.False(recording.HasThumbnail);
    }

    [Fact]
    public async Task A_finalize_that_failed_to_record_is_finalized_by_the_next_recovery()
    {
        Session session = CreateSession("562", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "562");
        _context.Records.WritesFailing = true;
        await Assert.ThrowsAnyAsync<Exception>(
            () => _context.Store.FinalizeAsync(session, CancellationToken.None));
        _context.Records.WritesFailing = false;

        (RecoveryService recovery, RecordingLibrary library) = Restart();
        IReadOnlyDictionary<string, RecoveredChannel> recovered = await recovery.RecoverAsync(
            [new ChannelEntry(Channel, Paused: true)], CancellationToken.None);

        Assert.Equal(session.Id, recovered[Channel].Finished!.Session.Id);
        SessionRecord? record = await _context.Store.TryLoadAsync(session.Directory, CancellationToken.None);
        Assert.Equal(SessionStatus.Complete, record!.Status);
        Assert.Equal(6.0, record.DurationSeconds);
        Assert.False(library.Find(session.Id)!.IsRecording);
    }

    [Fact]
    public async Task A_reopen_that_failed_to_record_stays_finished_after_a_restart()
    {
        Session session = CreateSession("563", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "563");
        await _context.Store.FinalizeAsync(session, CancellationToken.None);
        _context.Records.WritesFailing = true;
        await Assert.ThrowsAnyAsync<Exception>(
            () => _context.Store.ReopenAsync(session, CancellationToken.None));
        _context.Records.WritesFailing = false;

        (RecoveryService recovery, RecordingLibrary library) = Restart();
        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await recovery.RecoverAsync(Watched, CancellationToken.None);

        Assert.Null(recovered[Channel].Unfinished);
        Assert.Equal(session.Id, recovered[Channel].Finished!.Session.Id);
        Assert.Equal(2, recovered[Channel].Finished!.NextPart);
        Assert.True(File.Exists(Path.Combine(session.Directory, SessionStore.IndexFileName)));
        Assert.False(library.Find(session.Id)!.IsRecording);
    }

    [Fact]
    public async Task Complete_session_whose_only_segment_line_is_unterminated_is_skipped_and_kept()
    {
        Session session = CreateSession("667", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "667");
        await _context.Store.FinalizeAsync(session, CancellationToken.None);
        File.Delete(Path.Combine(session.Directory, SessionStore.IndexFileName));

        // The part's only segment line never got its line feed, so it has no segments by the
        // one definition. Recovery must not hand it to finalize, which would delete the folder.
        await File.WriteAllTextAsync(
            Path.Combine(session.Directory, "p001.m3u8"),
            "#EXTM3U\n#EXT-X-MAP:URI=\"init_001.mp4\"\n#EXTINF:6.0,\np001_00000.m4s");

        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await _recovery.RecoverAsync(Watched, CancellationToken.None);

        Assert.Null(recovered[Channel].Finished);
        Assert.Null(_context.Library.Find(session.Id));
        Assert.True(File.Exists(Path.Combine(session.Directory, "p001_00000.m4s")));
        Assert.NotNull(await _context.Store.TryLoadAsync(session.Directory, CancellationToken.None));
    }

    [Fact]
    public async Task Recording_session_keeps_an_init_file_named_by_a_map_with_attributes()
    {
        Session session = CreateSession("445", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "445");
        await File.WriteAllTextAsync(
            Path.Combine(session.Directory, "p001.m3u8"),
            "#EXTM3U\n#EXT-X-MAP:URI=\"init_001.mp4\",BYTERANGE=\"3@0\"\n#EXTINF:6.0,\n"
                + "p001_00000.m4s\n");

        await _recovery.RecoverAsync(Watched, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(session.Directory, "init_001.mp4")));
        Assert.True(File.Exists(Path.Combine(session.Directory, "p001_00000.m4s")));
    }

    [Fact]
    public async Task Complete_session_without_an_index_or_parts_is_not_served()
    {
        Session session = CreateSession("666", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "666");
        await _context.Store.FinalizeAsync(session, CancellationToken.None);

        foreach (string file in Directory.EnumerateFiles(session.Directory))
            File.Delete(file);

        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await _recovery.RecoverAsync(Watched, CancellationToken.None);

        Assert.Null(recovered[Channel].Finished);
        Assert.Null(_context.Library.Find(session.Id));
        Assert.True(_context.Library.IsLoaded);
    }

    [Fact]
    public async Task Complete_session_with_a_valid_index_becomes_finished()
    {
        Session session = CreateSession("990", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "990");
        await _context.Store.FinalizeAsync(session, CancellationToken.None);

        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await _recovery.RecoverAsync(Watched, CancellationToken.None);

        RecoveredChannel channel = recovered[Channel];
        Assert.Null(channel.Unfinished);
        Assert.NotNull(channel.Finished);
        Assert.Equal(session.Id, channel.Finished.Session.Id);
        Assert.Equal(2, channel.Finished.NextPart);
        Assert.True(channel.Finished.Complete);
        Assert.False(_context.Library.Find(session.Id)!.IsRecording);
    }

    [Fact]
    public async Task Complete_session_with_a_non_final_index_is_re_finalized()
    {
        Session session = CreateSession("991", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "991");
        await _context.Store.FinalizeAsync(session, CancellationToken.None);

        string indexPath = Path.Combine(session.Directory, SessionStore.IndexFileName);
        string index = await File.ReadAllTextAsync(indexPath);
        await File.WriteAllTextAsync(indexPath, index.Replace("#EXT-X-ENDLIST", ""));

        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await _recovery.RecoverAsync(Watched, CancellationToken.None);

        Assert.NotNull(recovered[Channel].Finished);
        string repaired = await File.ReadAllTextAsync(indexPath);
        Assert.Contains("#EXT-X-ENDLIST", repaired, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Complete_session_that_cannot_be_repaired_is_excluded_from_the_library()
    {
        Session session = CreateSession("992", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "992");
        await _context.Store.FinalizeAsync(session, CancellationToken.None);

        File.Delete(Path.Combine(session.Directory, SessionStore.IndexFileName));
        await File.WriteAllTextAsync(
            Path.Combine(session.Directory, "p001.m3u8"),
            "#EXTM3U\n#EXTINF:not-a-number,\np001_00000.m4s\n");

        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await _recovery.RecoverAsync(Watched, CancellationToken.None);

        Assert.Null(recovered[Channel].Finished);
        Assert.Null(_context.Library.Find(session.Id));
        Assert.True(_context.Library.IsLoaded);
        Assert.True(File.Exists(Path.Combine(session.Directory, "p001_00000.m4s")));
    }

    [Fact]
    public async Task Older_empty_session_is_not_returned_as_finished()
    {
        Session older = CreateSession("993", SessionStoreTestContext.StartedAt);
        LiveStream olderLive = new(
            "993", "Title 993", "Just Chatting", older.StartedAt, null);
        await _context.Store.OpenAsync(older, olderLive, CancellationToken.None);

        Session newer = CreateSession("994", SessionStoreTestContext.StartedAt.AddHours(1));
        await WriteRecordingAsync(newer, "994");

        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await _recovery.RecoverAsync(Watched, CancellationToken.None);

        RecoveredChannel channel = recovered[Channel];
        Assert.Equal(newer.Id, channel.Unfinished!.Session.Id);
        Assert.Null(channel.Finished);
        Assert.False(Directory.Exists(older.Directory));
    }

    [Fact]
    public async Task Finalize_failure_stops_recovery_instead_of_loading_a_partial_state()
    {
        Session older = CreateSession("888", SessionStoreTestContext.StartedAt);
        Session newer = CreateSession("889", SessionStoreTestContext.StartedAt.AddHours(1));
        await WriteRecordingAsync(older, "888");
        await WriteRecordingAsync(newer, "889");

        Directory.CreateDirectory(
            Path.Combine(older.Directory, SessionStore.IndexFileName + ".tmp"));

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(
            () => _recovery.RecoverAsync(Watched, CancellationToken.None));

        Assert.True(failure is UnauthorizedAccessException or IOException, failure.GetType().Name);
        Assert.False(_context.Library.IsLoaded);
    }

    [Fact]
    public async Task Paused_channel_unfinished_session_is_finalized_and_not_resumed()
    {
        Session session = CreateSession("996", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "996");

        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await _recovery.RecoverAsync(
                [new ChannelEntry(Channel, Paused: true)], CancellationToken.None);

        Assert.Null(recovered[Channel].Unfinished);
        Assert.NotNull(recovered[Channel].Finished);
        Assert.True(recovered[Channel].Finished!.Complete);
        SessionRecord? manifest = await _context.Store.TryLoadAsync(
            session.Directory, CancellationToken.None);
        Assert.Equal(SessionStatus.Complete, manifest!.Status);
        Assert.False(_context.Library.Find(session.Id)!.IsRecording);
    }

    [Fact]
    public async Task Unwatched_channel_sessions_are_finalized()
    {
        Session session = CreateSession("777", SessionStoreTestContext.StartedAt, channel: "removed");
        await WriteRecordingAsync(session, "777");

        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await _recovery.RecoverAsync(Watched, CancellationToken.None);

        Assert.Contains(Channel, recovered.Keys);
        Assert.Null(recovered[Channel].Unfinished);
        Assert.Null(recovered[Channel].Finished);

        SessionRecord? manifest = await _context.Store.TryLoadAsync(
            session.Directory, CancellationToken.None);
        Assert.Equal(SessionStatus.Complete, manifest!.Status);
        Assert.True(File.Exists(Path.Combine(session.Directory, SessionStore.IndexFileName)));
    }

    [Fact]
    public async Task Recovery_logs_one_summary_that_accounts_for_every_session()
    {
        Session older = CreateSession("111", SessionStoreTestContext.StartedAt);
        Session newer = CreateSession("222", SessionStoreTestContext.StartedAt.AddHours(1));
        Session repaired = CreateSession("333", SessionStoreTestContext.StartedAt.AddHours(-1));
        Session missing = CreateSession("444", SessionStoreTestContext.StartedAt.AddHours(-2));
        Session intact = CreateSession("555", SessionStoreTestContext.StartedAt.AddHours(-3));
        Session empty = CreateSession("666", SessionStoreTestContext.StartedAt.AddHours(-4));
        await WriteRecordingAsync(older, "111");
        await WriteRecordingAsync(newer, "222");
        await WriteRecordingAsync(repaired, "333");
        await _context.Store.FinalizeAsync(repaired, CancellationToken.None);
        File.Delete(Path.Combine(repaired.Directory, SessionStore.IndexFileName));
        await WriteRecordingAsync(missing, "444");
        Directory.Delete(missing.Directory, recursive: true);
        await WriteRecordingAsync(intact, "555");
        await _context.Store.FinalizeAsync(intact, CancellationToken.None);
        await _context.Store.OpenAsync(
            empty,
            new LiveStream("666", "Title 666", "Just Chatting", empty.StartedAt, null),
            CancellationToken.None);

        using var logs = new ListLoggerProvider();
        using var loggers = TestLogging.Factory(logs);
        await CreateRecovery(loggers).RecoverAsync(Watched, CancellationToken.None);

        CapturedLogEntry summary = Assert.Single(
            logs.Entries, entry => entry.Message.StartsWith("Recovered", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, summary.Level);
        Assert.Equal(6, summary.Properties["SessionCount"]);
        Assert.Equal(1, summary.Properties["CompleteCount"]);
        Assert.Equal(1, summary.Properties["ResumableCount"]);
        Assert.Equal(2, summary.Properties["FinalizedCount"]);
        Assert.Equal(1, summary.Properties["DeletedCount"]);
        Assert.Equal(1, summary.Properties["SkippedCount"]);
        Assert.Equal(0.0, summary.Properties["ElapsedMs"]);

        // The skip keeps its own warning; the summary does not replace it.
        Assert.Contains(logs.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains(missing.Id, StringComparison.Ordinal)
            && entry.Message.Contains("is missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recovery_of_an_empty_recordings_root_still_logs_its_summary()
    {
        using var logs = new ListLoggerProvider();
        using var loggers = TestLogging.Factory(logs);

        await CreateRecovery(loggers).RecoverAsync(Watched, CancellationToken.None);

        CapturedLogEntry summary = Assert.Single(
            logs.Entries, entry => entry.Message.StartsWith("Recovered", StringComparison.Ordinal));
        Assert.Equal(0, summary.Properties["SessionCount"]);
        Assert.Equal(0, summary.Properties["SkippedCount"]);
    }

    [Fact]
    public async Task Finalize_crash_window_recording_record_with_a_final_index_becomes_complete()
    {
        // Finalize wrote the index, then the record write failed (or the process died), so the
        // newest session of a watched, unpaused channel still says Recording.
        Session session = CreateSession("570", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "570");
        _context.Records.WritesFailing = true;
        await Assert.ThrowsAnyAsync<Exception>(
            () => _context.Store.FinalizeAsync(session, CancellationToken.None));
        _context.Records.WritesFailing = false;
        Assert.True(File.Exists(Path.Combine(session.Directory, SessionStore.IndexFileName)));

        (RecoveryService recovery, RecordingLibrary library) = Restart();
        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await recovery.RecoverAsync(Watched, CancellationToken.None);

        Assert.Null(recovered[Channel].Unfinished);
        Resumable finished = recovered[Channel].Finished!;
        Assert.Equal(session.Id, finished.Session.Id);
        Assert.True(finished.Complete);
        Assert.Equal(2, finished.NextPart);

        SessionRecord record = (await _context.Store.TryLoadAsync(session.Directory, CancellationToken.None))!;
        Assert.Equal(SessionStatus.Complete, record.Status);
        Assert.Equal(6.0, record.DurationSeconds);
        Assert.Equal(1, record.PartCount);
        Assert.Equal(1, record.SegmentCount);
        Assert.NotNull(record.SizeBytes);
        Assert.False(library.Find(session.Id)!.IsRecording);
    }

    [Fact]
    public async Task Reopen_crash_window_recording_record_with_a_stale_index_becomes_complete_again()
    {
        // Reopen marked the record Recording, then died before deleting the index.
        Session session = CreateSession("571", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "571");
        await _context.Store.FinalizeAsync(session, CancellationToken.None);
        string indexPath = Path.Combine(session.Directory, SessionStore.IndexFileName);
        string index = await File.ReadAllTextAsync(indexPath);
        await _context.Store.ReopenAsync(session, CancellationToken.None);
        await File.WriteAllTextAsync(indexPath, index);

        (RecoveryService recovery, RecordingLibrary library) = Restart();
        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await recovery.RecoverAsync(Watched, CancellationToken.None);

        Assert.Null(recovered[Channel].Unfinished);
        Assert.Equal(session.Id, recovered[Channel].Finished!.Session.Id);
        Assert.Equal(2, recovered[Channel].Finished!.NextPart);
        SessionRecord record = (await _context.Store.TryLoadAsync(session.Directory, CancellationToken.None))!;
        Assert.Equal(SessionStatus.Complete, record.Status);
        Assert.Equal(6.0, record.DurationSeconds);
        Assert.False(library.Find(session.Id)!.IsRecording);
    }

    [Fact]
    public async Task Complete_record_whose_index_was_lost_is_re_stitched_with_updated_aggregates()
    {
        // The index is gone and a second part was recorded after the stored aggregates.
        Session session = CreateSession("572", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "572");
        await _context.Store.FinalizeAsync(session, CancellationToken.None);
        File.Delete(Path.Combine(session.Directory, SessionStore.IndexFileName));
        await File.WriteAllTextAsync(
            Path.Combine(session.Directory, "p002.m3u8"),
            PlaylistTestData.Playlist(6, "init_002.mp4", PlaylistTestData.Segment("p002_00000.m4s", "2026-11-10T17:35:00Z", 6)));
        await File.WriteAllBytesAsync(Path.Combine(session.Directory, "init_002.mp4"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(session.Directory, "p002_00000.m4s"), [2, 3]);

        (RecoveryService recovery, RecordingLibrary library) = Restart();
        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await recovery.RecoverAsync(Watched, CancellationToken.None);

        Assert.Equal(3, recovered[Channel].Finished!.NextPart);
        SessionRecord record = (await _context.Store.TryLoadAsync(session.Directory, CancellationToken.None))!;
        Assert.Equal(SessionStatus.Complete, record.Status);
        Assert.Equal(12.0, record.DurationSeconds);
        Assert.Equal(2, record.PartCount);
        Assert.Equal(2, record.SegmentCount);

        RecordingSummary summary = library.Find(session.Id)!;
        Assert.Equal(12.0, summary.DurationSeconds);
        Assert.Equal(2, summary.PartCount);
        string index = await File.ReadAllTextAsync(Path.Combine(session.Directory, SessionStore.IndexFileName));
        Assert.Contains("p002_00000.m4s", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recording_record_whose_folder_is_missing_is_kept_and_not_listed_or_resumed()
    {
        Session session = CreateSession("573", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "573");
        Directory.Delete(session.Directory, recursive: true);

        (RecoveryService recovery, RecordingLibrary library) = Restart();
        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await recovery.RecoverAsync(Watched, CancellationToken.None);

        Assert.Null(recovered[Channel].Unfinished);
        Assert.Null(recovered[Channel].Finished);
        Assert.Null(library.Find(session.Id));
        Assert.False(Directory.Exists(session.Directory));
        SessionRecord? record = await _context.Store.TryLoadAsync(session.Directory, CancellationToken.None);
        Assert.Equal(SessionStatus.Recording, record!.Status);
    }

    [Fact]
    public async Task Older_recording_record_without_media_is_deleted_with_its_rows_and_folder()
    {
        Session empty = CreateSession("574", SessionStoreTestContext.StartedAt);
        await _context.Store.OpenAsync(
            empty,
            new LiveStream("574", "Title 574", "Just Chatting", empty.StartedAt, null),
            CancellationToken.None);
        Session newer = CreateSession("575", SessionStoreTestContext.StartedAt.AddHours(1));
        await WriteRecordingAsync(newer, "575");

        await _recovery.RecoverAsync(Watched, CancellationToken.None);

        Assert.Null(await _context.Store.TryLoadAsync(empty.Directory, CancellationToken.None));
        await using (var db = await _context.RecordStore.Contexts.CreateDbContextAsync())
        {
            Assert.Equal(0, db.SessionMetadataChanges.Count(change => change.SessionId == empty.Id));
        }

        Assert.False(Directory.Exists(empty.Directory));
        Assert.Null(_context.Library.Find(empty.Id));
    }

    [Fact]
    public async Task Older_recording_record_whose_only_segment_line_is_unterminated_keeps_its_media()
    {
        // A hard kill mid-line leaves a part with no complete segment but with segment media.
        // Finalize would treat the session as empty and delete the folder.
        Session older = CreateSession("576", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(older, "576");
        await File.WriteAllTextAsync(
            Path.Combine(older.Directory, "p001.m3u8"),
            "#EXTM3U\n#EXT-X-MAP:URI=\"init_001.mp4\"\n#EXTINF:6.0,\np001_00000.m4s");
        Session newer = CreateSession("577", SessionStoreTestContext.StartedAt.AddHours(1));
        await WriteRecordingAsync(newer, "577");

        await _recovery.RecoverAsync(Watched, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(older.Directory, "p001_00000.m4s")));
        Assert.NotNull(await _context.Store.TryLoadAsync(older.Directory, CancellationToken.None));
        Assert.Null(_context.Library.Find(older.Id));
    }

    [Fact]
    public async Task Recording_record_with_a_final_index_but_no_part_playlists_keeps_its_media()
    {
        Session session = CreateSession("578", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "578");
        _context.Records.WritesFailing = true;
        await Assert.ThrowsAnyAsync<Exception>(
            () => _context.Store.FinalizeAsync(session, CancellationToken.None));
        _context.Records.WritesFailing = false;
        File.Delete(Path.Combine(session.Directory, "p001.m3u8"));

        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await _recovery.RecoverAsync(Watched, CancellationToken.None);

        Assert.Null(recovered[Channel].Unfinished);
        Assert.Null(recovered[Channel].Finished);
        Assert.True(File.Exists(Path.Combine(session.Directory, "p001_00000.m4s")));
        Assert.True(File.Exists(Path.Combine(session.Directory, "init_001.mp4")));
        Assert.NotNull(await _context.Store.TryLoadAsync(session.Directory, CancellationToken.None));
        Assert.Null(_context.Library.Find(session.Id));
    }

    private RecoveryService CreateRecovery(ILoggerFactory loggers) =>
        new(
            _context.Store,
            _context.Library,
            Options.Create(new StorageOptions { RecordingsRoot = _context.Root }),
            _context.Clock,
            loggers.CreateLogger<RecoveryService>());

    /// <summary>A fresh library, store, and recovery over the same record store and root.</summary>
    private (RecoveryService Recovery, RecordingLibrary Library) Restart()
    {
        RecordingLibrary library = _context.CreateLibrary();
        return (
            new RecoveryService(
                _context.CreateStore(library),
                library,
                Options.Create(new StorageOptions { RecordingsRoot = _context.Root }),
                _context.Clock,
                NullLogger<RecoveryService>.Instance),
            library);
    }

    private static async Task<string> WriteImageAsync(Session session)
    {
        string image = Path.Combine(session.Directory, "grab.jpg");
        await File.WriteAllBytesAsync(image, [0xFF, 0xD8, 0xFF]);
        return image;
    }

    private Session CreateSession(
        string streamId, DateTimeOffset startedAt, string channel = Channel)
    {
        LiveStream live = new(streamId, $"Title {streamId}", "Just Chatting", startedAt, null);
        return Session.For(live, channel, _context.Root);
    }

    private async Task WriteRecordingAsync(
        Session session, string streamId, bool validPart = true)
    {
        LiveStream live = new(streamId, $"Title {streamId}", "Just Chatting", session.StartedAt, null);
        await _context.Store.OpenAsync(session, live, CancellationToken.None);
        Directory.CreateDirectory(session.Directory);
        WritePart(session, validPart);
    }

    private static void WritePart(Session session, bool validPart)
    {
        string playlist = validPart
            ? "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:6\n"
                + "#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:EVENT\n"
                + "#EXT-X-INDEPENDENT-SEGMENTS\n"
                + "#EXT-X-MAP:URI=\"init_001.mp4\"\n#EXTINF:6.0,\n"
                + "p001_00000.m4s\n"
            : "#EXTM3U\n#EXTINF:not-a-number,\np001_00000.m4s\n";

        File.WriteAllText(Path.Combine(session.Directory, "p001.m3u8"), playlist);
        File.WriteAllBytes(Path.Combine(session.Directory, "init_001.mp4"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(session.Directory, "p001_00000.m4s"), [4, 5, 6]);
    }
}
