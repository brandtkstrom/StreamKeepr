using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Hosting;
using static StreamKeepr.Tests.Storage.PlaylistTestData;

namespace StreamKeepr.Tests.Storage;

/// <summary>
/// The live index against real session folders. Only part reads go through
/// <see cref="ScriptedPartSource"/>, to count, fail, or hold them.
/// </summary>
public sealed class LiveIndexTests
{
    [Fact]
    public async Task Refresh_publishes_the_stitched_body_and_counts_segments()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        LiveIndex index = NewIndex(context);

        await index.RefreshAllAsync(CancellationToken.None);

        LiveIndexSnapshot snapshot = Assert.IsType<LiveIndexSnapshot>(index.Find(context.Session.Id));
        Assert.Equal(1, snapshot.Version);
        Assert.Equal(1, snapshot.SegmentCount);
        Assert.Contains("p001_00001.m4s", snapshot.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-ENDLIST", snapshot.Body, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', snapshot.Body);
        Assert.Equal(Encoding.UTF8.GetBytes(snapshot.Body), snapshot.Utf8.ToArray());
        Assert.Equal(context.Clock.GetUtcNow(), snapshot.RefreshedAt);
    }

    [Fact]
    public async Task A_warm_refresh_reads_no_part_content()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        var source = new ScriptedPartSource();
        LiveIndex index = NewIndex(context, source);

        string? first = await index.RefreshOneAsync(Summary(context), CancellationToken.None);
        Assert.Equal([context.PartPath(1)], source.Reads);

        source.Reset();
        string? second = await index.RefreshOneAsync(Summary(context), CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Empty(source.Reads);
    }

    [Fact]
    public async Task A_grown_last_part_is_the_only_part_read_and_equals_a_full_stitch()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        context.WriteLivePart(2, Segment("p002_00001.m4s", "2026-11-10T18:30:06Z", 6));
        await OpenAndLoadAsync(context);
        var source = new ScriptedPartSource();
        LiveIndex index = NewIndex(context, source);
        await index.RefreshAllAsync(CancellationToken.None);
        source.Reset();

        context.WriteLivePart(2,
            Segment("p002_00001.m4s", "2026-11-10T18:30:06Z", 6),
            Segment("p002_00002.m4s", "2026-11-10T18:30:12Z", 6));
        string? body = await index.RefreshOneAsync(Summary(context), CancellationToken.None);

        Assert.Equal([context.PartPath(2)], source.Reads);
        Assert.Equal(await FullStitchAsync(context), body);
        Assert.Contains("p002_00002.m4s", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_closed_part_that_changes_on_disk_is_not_read_again()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        context.WritePart(2, Segment("p002_00001.m4s", "2026-11-10T18:30:06Z", 6));
        await OpenAndLoadAsync(context);
        LiveIndex index = NewIndex(context);
        await index.RefreshAllAsync(CancellationToken.None);

        // p001 is closed while a higher part exists. Changing it on disk must not change the
        // body because only the active last part is re-read.
        await File.WriteAllTextAsync(
            context.PartPath(1),
            Playlist(6, "init_001.mp4", Segment("p001_99999.m4s", "2026-11-10T18:30:00Z", 6)));
        context.WritePart(2,
            Segment("p002_00002.m4s", "2026-11-10T18:30:12Z", 6),
            Segment("p002_00003.m4s", "2026-11-10T18:30:18Z", 6));

        await index.RefreshAllAsync(CancellationToken.None);

        string body = Assert.IsType<LiveIndexSnapshot>(index.Find(context.Session.Id)).Body;
        Assert.Contains("p001_00001.m4s", body, StringComparison.Ordinal);
        Assert.DoesNotContain("p001_99999.m4s", body, StringComparison.Ordinal);
        Assert.Contains("p002_00002.m4s", body, StringComparison.Ordinal);
        Assert.Contains("p002_00003.m4s", body, StringComparison.Ordinal);
        Assert.DoesNotContain("p002_00001.m4s", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_part_adds_one_discontinuity_and_is_the_only_part_read()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        var source = new ScriptedPartSource();
        LiveIndex index = NewIndex(context, source);
        LiveIndexSnapshot first = await RefreshAsync(index, context);
        source.Reset();

        context.WriteLivePart(2, Segment("p002_00001.m4s", "2026-11-10T18:30:06Z", 6));
        LiveIndexSnapshot second = await RefreshAsync(index, context);

        Assert.Equal([context.PartPath(2)], source.Reads);
        Assert.Equal(await FullStitchAsync(context), second.Body);
        Assert.Equal(1, SessionStoreTestContext.Occurrences(second.Body, "#EXT-X-DISCONTINUITY"));
        Assert.True(
            second.Body.IndexOf("#EXT-X-DISCONTINUITY", StringComparison.Ordinal)
            < second.Body.IndexOf("#EXT-X-MAP:URI=\"init_002.mp4\"", StringComparison.Ordinal));
        Assert.Equal(2, second.Version);
        Assert.Equal(2, second.SegmentCount);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task A_deleted_part_disappears_from_the_live_index()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        context.WritePart(2, Segment("p002_00001.m4s", "2026-11-10T18:30:06Z", 6));
        await OpenAndLoadAsync(context);
        LiveIndex index = NewIndex(context);
        await index.RefreshAllAsync(CancellationToken.None);

        File.Delete(context.PartPath(1));
        await index.RefreshAllAsync(CancellationToken.None);

        string body = Assert.IsType<LiveIndexSnapshot>(index.Find(context.Session.Id)).Body;
        Assert.Contains("p002_00001.m4s", body, StringComparison.Ordinal);
        Assert.DoesNotContain("p001_00001.m4s", body, StringComparison.Ordinal);
        Assert.Equal(0, SessionStoreTestContext.Occurrences(body, "#EXT-X-DISCONTINUITY"));
    }

    [Fact]
    public async Task A_temp_file_is_never_read_as_a_part()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        var source = new ScriptedPartSource();
        LiveIndex index = NewIndex(context, source);
        LiveIndexSnapshot first = await RefreshAsync(index, context);

        await File.WriteAllTextAsync(
            context.PartPath(1) + ".tmp",
            LivePlaylist(20, "init_001.mp4", Segment("p001_99999.m4s", "2026-11-10T18:30:00Z", 6)));
        source.Reset();
        LiveIndexSnapshot second = await RefreshAsync(index, context);

        Assert.Empty(source.Reads);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task Refresh_does_not_take_the_manifest_lock()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        LiveIndex index = NewIndex(context);

        var gate = context.Store.LockFor(context.Directory);
        await gate.WaitAsync();
        try
        {
            await index.RefreshAllAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.NotNull(index.Find(context.Session.Id));
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task A_refresh_that_races_finalize_publishes_nothing()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        RecordingSummary stale = Summary(context);
        LiveIndex index = NewIndex(context);

        // The manifest still says Recording, but the library already records Complete: the
        // library is the authority, so the stale refresh must not publish a live body.
        context.Library.Upsert(stale with { IsRecording = false });
        string? body = await index.RefreshOneAsync(stale, CancellationToken.None);

        Assert.Null(body);
        Assert.Null(index.Find(stale.Id));
        Assert.Equal(0, index.SessionCount);
    }

    [Fact]
    public async Task Refresh_skips_unchanged_bodies()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        LiveIndex index = NewIndex(context);
        LiveIndexSnapshot first = await RefreshAsync(index, context);
        context.Clock.Advance(TimeSpan.FromSeconds(5));

        await index.RefreshAllAsync(CancellationToken.None);

        Assert.Same(first, index.Find(context.Session.Id));
        Assert.Equal(1, first.Version);
    }

    [Fact]
    public async Task Finalize_removes_the_entry_and_reopen_rebuilds_from_disk()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        var source = new ScriptedPartSource();
        LiveIndex index = NewIndex(context, source);
        await index.RefreshAllAsync(CancellationToken.None);
        Assert.NotEmpty(source.Reads);

        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        Assert.False(Summary(context).IsRecording);
        await index.RefreshAllAsync(CancellationToken.None);
        Assert.Null(index.Find(context.Session.Id));
        Assert.Equal(0, index.SessionCount);

        source.Reset();
        await context.Store.ReopenAsync(context.Session, CancellationToken.None);
        await index.RefreshAllAsync(CancellationToken.None);

        Assert.Equal([context.PartPath(1)], source.Reads);
        string body = Assert.IsType<LiveIndexSnapshot>(index.Find(context.Session.Id)).Body;
        Assert.Contains("p001_00001.m4s", body, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-ENDLIST", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_that_leaves_the_library_is_evicted()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        LiveIndex index = NewIndex(context);
        await index.RefreshAllAsync(CancellationToken.None);
        Assert.Equal(1, index.SessionCount);

        context.Library.Remove(context.Session.Id);
        await index.RefreshAllAsync(CancellationToken.None);

        Assert.Null(index.Find(context.Session.Id));
        Assert.Equal(0, index.SessionCount);
    }

    [Fact]
    public async Task A_deleted_session_folder_drops_the_live_entry()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        LiveIndex index = NewIndex(context);
        await index.RefreshAllAsync(CancellationToken.None);

        Directory.Delete(context.Directory, recursive: true);
        string? body = await index.RefreshOneAsync(Summary(context), CancellationToken.None);

        Assert.Null(body);
        Assert.Null(index.Find(context.Session.Id));
        Assert.Equal(0, index.SessionCount);
    }

    [Fact]
    public async Task A_reopened_session_starts_without_the_old_failure_backoff()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        var source = new ScriptedPartSource();
        LiveIndex index = NewIndex(context, source);
        source.ThrowOn(context.PartPath(1), new IOException("EIO"));
        await index.RefreshAllAsync(CancellationToken.None);
        Assert.Null(index.Find(context.Session.Id));

        // The session finalizes and reopens inside the backoff window. Its old failure state
        // left with its entry, so the new lifecycle refreshes at once.
        source.StopThrowing(context.PartPath(1));
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        await index.RefreshAllAsync(CancellationToken.None);
        await context.Store.ReopenAsync(context.Session, CancellationToken.None);
        await index.RefreshAllAsync(CancellationToken.None);

        Assert.NotNull(index.Find(context.Session.Id));
    }

    [Fact]
    public async Task Refresh_failure_keeps_the_previous_snapshot()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        var source = new ScriptedPartSource();
        var logs = new ListLoggerProvider();
        using ILoggerFactory loggers = TestLogging.Factory(logs);
        LiveIndex index = NewIndex(context, source, loggers: loggers);
        LiveIndexSnapshot published = await RefreshAsync(index, context);

        GrowLastPart(context);
        source.ThrowOn(context.PartPath(1), new IOException("EIO"));
        await index.RefreshAllAsync(CancellationToken.None);

        Assert.Same(published, index.Find(context.Session.Id));
        Assert.Contains(
            logs.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains(context.Session.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refresh_treats_a_timeout_exception_as_a_failure()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        var source = new ScriptedPartSource();
        var logs = new ListLoggerProvider();
        using ILoggerFactory loggers = TestLogging.Factory(logs);
        LiveIndex index = NewIndex(context, source, loggers: loggers);
        LiveIndexSnapshot published = await RefreshAsync(index, context);

        GrowLastPart(context);
        source.ThrowOn(context.PartPath(1), new TimeoutException("slow"));
        await index.RefreshAllAsync(CancellationToken.None);

        Assert.Same(published, index.Find(context.Session.Id));
        Assert.Contains(
            logs.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains(context.Session.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refresh_keeps_last_known_good_when_the_manifest_is_unreadable()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        LiveIndex index = NewIndex(context);
        LiveIndexSnapshot published = await RefreshAsync(index, context);

        await File.WriteAllTextAsync(context.ManifestPath, "{ not json");
        await index.RefreshAllAsync(CancellationToken.None);

        Assert.Same(published, index.Find(context.Session.Id));
    }

    [Fact]
    public async Task Refresh_timeout_keeps_the_last_known_good_body()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        var source = new ScriptedPartSource();
        var options = new LivePlaylistOptions { RefreshTimeoutSeconds = 2 };
        LiveIndex index = NewIndex(context, source, options);
        LiveIndexSnapshot published = await RefreshAsync(index, context);

        GrowLastPart(context);
        source.BlockAll();
        Task refresh = index.RefreshOneAsync(Summary(context), CancellationToken.None);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        context.Clock.Advance(TimeSpan.FromSeconds(options.RefreshTimeoutSeconds));
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(published, index.Find(context.Session.Id));
    }

    [Fact]
    public async Task Refresh_is_single_flight_per_session()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        var source = new ScriptedPartSource();
        source.BlockAll();
        LiveIndex index = NewIndex(context, source);

        Task first = index.RefreshOneAsync(Summary(context), CancellationToken.None);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // A busy session skips the tick instead of queueing behind the slow read.
        await index.RefreshOneAsync(Summary(context), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(source.Reads);

        source.Release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(index.Find(context.Session.Id));
        Assert.Single(source.Reads);
    }

    [Fact]
    public async Task One_session_failing_does_not_stop_another_session_refreshing()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        Session other = await OpenOtherSessionAsync(context);
        var source = new ScriptedPartSource();
        source.ThrowOn(context.PartPath(1), new IOException("EIO"));
        LiveIndex index = NewIndex(context, source);

        await index.RefreshAllAsync(CancellationToken.None);

        Assert.Null(index.Find(context.Session.Id));
        Assert.NotNull(index.Find(other.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Slow_session_does_not_block_another_session(bool firstIsSlow)
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        Session other = await OpenOtherSessionAsync(context);
        (string slow, string fast) = firstIsSlow
            ? (context.Session.Id, other.Id)
            : (other.Id, context.Session.Id);
        string slowPart = Path.Combine(
            firstIsSlow ? context.Directory : other.Directory, "p001.m3u8");
        var source = new ScriptedPartSource();
        source.BlockOn(slowPart);
        LiveIndex index = NewIndex(context, source);

        // Both orders run, so the slow session is refreshed first in one of them.
        Task refresh = index.RefreshAllAsync(CancellationToken.None);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => index.Find(fast) is not null);
        Assert.Null(index.Find(slow));

        source.Release.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(index.Find(slow));
    }

    [Fact]
    public async Task A_failed_session_is_not_retried_until_its_backoff_elapses()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        var source = new ScriptedPartSource();
        LiveIndex index = NewIndex(context, source);
        LiveIndexSnapshot published = await RefreshAsync(index, context);

        GrowLastPart(context);
        source.ThrowOn(context.PartPath(1), new IOException("EIO"));
        await index.RefreshAllAsync(CancellationToken.None);
        source.StopThrowing(context.PartPath(1));
        source.Reset();

        // The first failure backs off for one refresh interval (1 s by default).
        await index.RefreshAllAsync(CancellationToken.None);
        Assert.Empty(source.Reads);
        Assert.Same(published, index.Find(context.Session.Id));

        context.Clock.Advance(TimeSpan.FromSeconds(1));
        await index.RefreshAllAsync(CancellationToken.None);
        Assert.Equal([context.PartPath(1)], source.Reads);
        Assert.Contains(
            "p001_00002.m4s", index.Find(context.Session.Id)!.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failures_log_one_warning_per_window_and_debug_afterwards()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        var logs = new ListLoggerProvider();
        using ILoggerFactory loggers = TestLogging.Factory(logs);
        LiveIndex index = NewIndex(context, loggers: loggers);
        string manifest = await File.ReadAllTextAsync(context.ManifestPath);

        await index.RefreshAllAsync(CancellationToken.None);

        await File.WriteAllTextAsync(context.ManifestPath, "{ not json");
        await index.RefreshAllAsync(CancellationToken.None);
        context.Clock.Advance(TimeSpan.FromSeconds(2));
        await index.RefreshAllAsync(CancellationToken.None);

        context.Clock.Advance(TimeSpan.FromSeconds(3));
        await File.WriteAllTextAsync(context.ManifestPath, manifest);
        await index.RefreshAllAsync(CancellationToken.None);

        await File.WriteAllTextAsync(context.ManifestPath, "{ not json");
        await index.RefreshAllAsync(CancellationToken.None);

        CapturedLogEntry[] sessionLogs = logs.Entries
            .Where(entry => entry.Message.Contains(context.Session.Id, StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, sessionLogs.Count(entry => entry.Level == LogLevel.Warning));
        Assert.Equal(1, sessionLogs.Count(entry => entry.Level == LogLevel.Debug));
        Assert.DoesNotContain(sessionLogs, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Get_or_refresh_returns_the_live_body_for_a_recording()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        LiveIndex index = NewIndex(context);

        string? playlist = await index.GetOrRefreshAsync(Summary(context), CancellationToken.None);

        Assert.NotNull(playlist);
        Assert.Contains("p001_00001.m4s", playlist, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-ENDLIST", playlist, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_or_refresh_returns_the_session_index_for_a_complete_session()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        LiveIndex index = NewIndex(context);

        string? playlist = await index.GetOrRefreshAsync(Summary(context), CancellationToken.None);

        Assert.Equal(await File.ReadAllTextAsync(context.IndexPath), playlist);
        Assert.Equal(1, SessionStoreTestContext.Occurrences(playlist!, "#EXT-X-ENDLIST"));
        Assert.Null(index.Find(context.Session.Id));
        Assert.Equal(0, index.SessionCount);
    }

    [Fact]
    public async Task Get_or_refresh_returns_null_for_a_complete_session_without_an_index()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        File.Delete(context.IndexPath);
        LiveIndex index = NewIndex(context);

        Assert.Null(await index.GetOrRefreshAsync(Summary(context), CancellationToken.None));
    }

    [Fact]
    public async Task Get_or_refresh_returns_the_session_index_when_a_recording_races_finalize()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        RecordingSummary stale = Summary(context);
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        LiveIndex index = NewIndex(context);

        string? result = await index.GetOrRefreshAsync(stale, CancellationToken.None);

        Assert.Equal(await File.ReadAllTextAsync(context.IndexPath), result);
        Assert.Null(index.Find(stale.Id));
    }

    [Fact]
    public async Task Concurrent_finds_only_see_whole_published_bodies()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        LiveIndex index = NewIndex(context);
        string bodyA = (await RefreshAsync(index, context)).Body;
        GrowLastPart(context);
        string bodyB = (await RefreshAsync(index, context)).Body;
        Assert.NotEqual(bodyA, bodyB);

        using var stop = new CancellationTokenSource();
        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                LiveIndexSnapshot read = Assert.IsType<LiveIndexSnapshot>(index.Find(context.Session.Id));
                Assert.True(read.Body == bodyA || read.Body == bodyB);
                Assert.Equal(Encoding.UTF8.GetBytes(read.Body), read.Utf8.ToArray());
            }
        })).ToArray();

        for (int i = 0; i < 40; i++)
        {
            if (i % 2 == 0)
                context.WriteLivePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
            else
                GrowLastPart(context);
            await index.RefreshOneAsync(Summary(context), CancellationToken.None);
        }

        stop.Cancel();
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task The_refresher_ticks_on_the_injected_clock()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1, Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAndLoadAsync(context);
        LiveIndex index = NewIndex(context);
        using var refresher = new LiveIndexRefresher(index, context.Clock);

        await refresher.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => index.Find(context.Session.Id) is not null);

            GrowLastPart(context);
            await Task.Delay(50);
            Assert.DoesNotContain(
                "p001_00002.m4s", index.Find(context.Session.Id)!.Body, StringComparison.Ordinal);

            context.Clock.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => index.Find(context.Session.Id)!.Body
                .Contains("p001_00002.m4s", StringComparison.Ordinal));
        }
        finally
        {
            await refresher.StopAsync(CancellationToken.None);
        }
    }

    internal static LiveIndex NewIndex(
        SessionStoreTestContext context,
        IPartPlaylistSource? source = null,
        LivePlaylistOptions? options = null,
        ILoggerFactory? loggers = null) =>
        new(
            context.Library,
            context.Store,
            Options.Create(new StorageOptions { RecordingsRoot = context.Root }),
            Options.Create(options ?? new LivePlaylistOptions()),
            context.Clock,
            (loggers ?? NullLoggerFactory.Instance).CreateLogger<LiveIndex>(),
            source ?? FilePartPlaylistSource.Instance);

    private static async Task<LiveIndexSnapshot> RefreshAsync(
        LiveIndex index, SessionStoreTestContext context)
    {
        await index.RefreshOneAsync(Summary(context), CancellationToken.None);
        return Assert.IsType<LiveIndexSnapshot>(index.Find(context.Session.Id));
    }

    /// <summary>Rewrites part 1 with a second segment, so its length and body change.</summary>
    private static void GrowLastPart(SessionStoreTestContext context) =>
        context.WriteLivePart(1,
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6),
            Segment("p001_00002.m4s", "2026-11-10T18:30:06Z", 6));

    private static async Task<Session> OpenOtherSessionAsync(SessionStoreTestContext context)
    {
        var live = new LiveStream(
            "223456789", "Other", "Just Chatting", SessionStoreTestContext.StartedAt.AddMinutes(1), null);
        Session other = Session.For(live, "streamer", context.Root);
        Directory.CreateDirectory(other.Directory);
        await File.WriteAllTextAsync(
            Path.Combine(other.Directory, "p001.m3u8"),
            Playlist(6, "init_001.mp4", Segment("p001_00001.m4s", "2026-11-10T18:31:00Z", 6)));
        await context.Store.OpenAsync(other, live, CancellationToken.None);
        return other;
    }

    private static RecordingSummary Summary(SessionStoreTestContext context) =>
        Assert.IsType<RecordingSummary>(context.Library.Find(context.Session.Id));

    private static async Task OpenAndLoadAsync(SessionStoreTestContext context)
    {
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();
    }

    private static async Task<string> FullStitchAsync(SessionStoreTestContext context)
    {
        var files = context.Store.PartPlaylistFiles(context.Directory);
        var parts = new List<PartPlaylist>(files.Count);
        foreach ((int part, string path) in files)
            parts.Add(new PartPlaylist(part, await File.ReadAllTextAsync(path)));
        return PlaylistStitcher.Stitch(parts, context.Store.MinTargetDuration, final: false);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        throw new TimeoutException("timed out waiting for the live index");
    }
}
