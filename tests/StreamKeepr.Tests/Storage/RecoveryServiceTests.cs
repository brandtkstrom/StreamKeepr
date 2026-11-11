using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

public sealed class RecoveryServiceTests : IDisposable
{
    private const string Channel = "streamer";

    private readonly SessionStoreTestContext _context = new();
    private readonly RecoveryService _recovery;

    public RecoveryServiceTests()
    {
        _recovery = new RecoveryService(
            _context.Store,
            _context.Library,
            Options.Create(new StorageOptions { RecordingsRoot = _context.Root }),
            NullLogger<RecoveryService>.Instance);
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
            await _recovery.RecoverAsync([Channel], CancellationToken.None);

        RecoveredChannel channel = recovered[Channel];
        Assert.NotNull(channel.Unfinished);
        Assert.Equal(newer.Id, channel.Unfinished.Session.Id);
        Assert.Equal(2, channel.Unfinished.NextPart);
        Assert.False(channel.Unfinished.Complete);

        Assert.NotNull(channel.Finished);
        Assert.Equal(older.Id, channel.Finished.Session.Id);
        Assert.True(channel.Finished.Complete);

        SessionManifest? olderManifest = await _context.Store.TryLoadAsync(
            older.Directory, CancellationToken.None);
        Assert.Equal(SessionStatus.Complete, olderManifest!.Status);
        Assert.True(File.Exists(Path.Combine(older.Directory, SessionStore.IndexFileName)));

        SessionManifest? newerManifest = await _context.Store.TryLoadAsync(
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
            await _recovery.RecoverAsync([Channel], CancellationToken.None);

        RecoveredChannel channel = recovered[Channel];
        Assert.Null(channel.Unfinished);
        Assert.NotNull(channel.Finished);
        Assert.Equal(session.Id, channel.Finished.Session.Id);
        Assert.True(channel.Finished.Complete);
        Assert.True(File.Exists(Path.Combine(session.Directory, SessionStore.IndexFileName)));

        SessionManifest? manifest = await _context.Store.TryLoadAsync(
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
            await _recovery.RecoverAsync([Channel], CancellationToken.None);

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
    public async Task Corrupt_or_missing_manifests_and_unparseable_parts_are_skipped()
    {
        Session corrupt = CreateSession("555", SessionStoreTestContext.StartedAt);
        Directory.CreateDirectory(corrupt.Directory);
        string corruptMedia = Path.Combine(corrupt.Directory, "keep.m4s");
        await File.WriteAllBytesAsync(corruptMedia, [1, 2, 3]);
        await File.WriteAllTextAsync(
            Path.Combine(corrupt.Directory, SessionStore.ManifestFileName), "not json");

        Session missing = CreateSession("556", SessionStoreTestContext.StartedAt.AddMinutes(1));
        Directory.CreateDirectory(missing.Directory);
        string missingMedia = Path.Combine(missing.Directory, "keep.m4s");
        await File.WriteAllBytesAsync(missingMedia, [1, 2, 3]);

        Session badPart = CreateSession("557", SessionStoreTestContext.StartedAt.AddMinutes(2));
        await WriteRecordingAsync(badPart, "557", validPart: false);
        string badTemp = Path.Combine(badPart.Directory, "stale.tmp");
        await File.WriteAllTextAsync(badTemp, "stale");

        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await _recovery.RecoverAsync([Channel], CancellationToken.None);

        Assert.Null(recovered[Channel].Unfinished);
        Assert.Null(recovered[Channel].Finished);
        Assert.True(File.Exists(corruptMedia));
        Assert.True(File.Exists(missingMedia));
        Assert.False(File.Exists(badTemp));
        Assert.True(File.Exists(Path.Combine(badPart.Directory, "p001_00000.m4s")));
        Assert.True(_context.Library.IsLoaded);
    }

    [Fact]
    public async Task Complete_session_without_an_index_or_parts_is_not_served()
    {
        Session session = CreateSession("666", SessionStoreTestContext.StartedAt);
        await WriteRecordingAsync(session, "666");
        await _context.Store.FinalizeAsync(session, CancellationToken.None);

        foreach (string file in Directory.EnumerateFiles(session.Directory))
        {
            string name = Path.GetFileName(file);
            if (name != SessionStore.ManifestFileName)
                File.Delete(file);
        }

        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await _recovery.RecoverAsync([Channel], CancellationToken.None);

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
            await _recovery.RecoverAsync([Channel], CancellationToken.None);

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
            await _recovery.RecoverAsync([Channel], CancellationToken.None);

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
            await _recovery.RecoverAsync([Channel], CancellationToken.None);

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
            await _recovery.RecoverAsync([Channel], CancellationToken.None);

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
            () => _recovery.RecoverAsync([Channel], CancellationToken.None));

        Assert.True(failure is UnauthorizedAccessException or IOException, failure.GetType().Name);
        Assert.False(_context.Library.IsLoaded);
    }

    [Fact]
    public async Task Unwatched_channel_sessions_are_finalized()
    {
        Session session = CreateSession("777", SessionStoreTestContext.StartedAt, channel: "removed");
        await WriteRecordingAsync(session, "777");

        IReadOnlyDictionary<string, RecoveredChannel> recovered =
            await _recovery.RecoverAsync([Channel], CancellationToken.None);

        Assert.Contains(Channel, recovered.Keys);
        Assert.Null(recovered[Channel].Unfinished);
        Assert.Null(recovered[Channel].Finished);

        SessionManifest? manifest = await _context.Store.TryLoadAsync(
            session.Directory, CancellationToken.None);
        Assert.Equal(SessionStatus.Complete, manifest!.Status);
        Assert.True(File.Exists(Path.Combine(session.Directory, SessionStore.IndexFileName)));
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
