using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Storage;

public sealed class LivePlaylistServiceTests
{
    [Fact]
    public async Task Refresh_publishes_the_stitched_body_and_counts_segments()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();
        var cache = new LivePlaylistCache();
        LivePlaylistService service = NewService(context, cache, RealIo(context));

        await service.RefreshAllAsync(CancellationToken.None);

        LivePlaylistSnapshot snapshot = Assert.IsType<LivePlaylistSnapshot>(
            cache.Find(context.Session.Id));
        Assert.Equal(1, snapshot.Version);
        Assert.Equal(1, snapshot.SegmentCount);
        Assert.Contains("p001_00001.m4s", snapshot.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-ENDLIST", snapshot.Body, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', snapshot.Body);
        Assert.Equal(Encoding.UTF8.GetBytes(snapshot.Body), snapshot.Utf8.ToArray());
        Assert.Equal(context.Clock.GetUtcNow(), snapshot.RefreshedAt);
    }

    [Fact]
    public async Task Refresh_reads_only_the_changed_last_part()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        context.WritePart(
            2, PlaylistTestData.Segment("p002_00001.m4s", "2026-11-10T18:30:06Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();
        var cache = new LivePlaylistCache();
        LivePlaylistService service = NewService(context, cache, RealIo(context));
        await service.RefreshAllAsync(CancellationToken.None);

        // p001 is closed while a higher part exists. Changing it on disk must not change the
        // body because only the active last part is re-read.
        await File.WriteAllTextAsync(
            context.PartPath(1),
            PlaylistTestData.Playlist(
                6,
                "init_001.mp4",
                PlaylistTestData.Segment(
                    "p001_99999.m4s", "2026-11-10T18:30:00Z", 6)));
        context.WritePart(
            2,
            PlaylistTestData.Segment("p002_00002.m4s", "2026-11-10T18:30:12Z", 6),
            PlaylistTestData.Segment("p002_00003.m4s", "2026-11-10T18:30:18Z", 6));

        await service.RefreshAllAsync(CancellationToken.None);

        LivePlaylistSnapshot snapshot = Assert.IsType<LivePlaylistSnapshot>(
            cache.Find(context.Session.Id));
        Assert.Contains("p001_00001.m4s", snapshot.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("p001_99999.m4s", snapshot.Body, StringComparison.Ordinal);
        Assert.Contains("p002_00002.m4s", snapshot.Body, StringComparison.Ordinal);
        Assert.Contains("p002_00003.m4s", snapshot.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("p002_00001.m4s", snapshot.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_rebuilds_when_a_part_disappears()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        context.WritePart(
            2, PlaylistTestData.Segment("p002_00001.m4s", "2026-11-10T18:30:06Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();
        var cache = new LivePlaylistCache();
        LivePlaylistService service = NewService(context, cache, RealIo(context));
        await service.RefreshAllAsync(CancellationToken.None);
        Assert.NotNull(cache.Find(context.Session.Id));

        File.Delete(context.PartPath(1));
        await service.RefreshAllAsync(CancellationToken.None);

        LivePlaylistSnapshot snapshot = Assert.IsType<LivePlaylistSnapshot>(
            cache.Find(context.Session.Id));
        Assert.Contains("p002_00001.m4s", snapshot.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("p001_00001.m4s", snapshot.Body, StringComparison.Ordinal);
        Assert.Equal(0, SessionStoreTestContext.Occurrences(snapshot.Body, "#EXT-X-DISCONTINUITY"));
    }

    [Fact]
    public async Task Refresh_does_not_take_the_manifest_lock()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();
        var cache = new LivePlaylistCache();
        LivePlaylistService service = NewService(context, cache, RealIo(context));

        var gate = context.Store.LockFor(context.Directory);
        await gate.WaitAsync();
        try
        {
            await service.RefreshAllAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.NotNull(cache.Find(context.Session.Id));
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task Complete_session_publishes_nothing()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();
        RecordingSummary stale = RecordingSummary(context);
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        var cache = new LivePlaylistCache();
        var io = new FakeLivePlaylistIo
        {
            Handler = static (_, _) => Task.FromResult(
                LivePlaylistRead.Live(PlaylistBody("p001_00002.m4s"))),
        };
        LivePlaylistService service = NewService(context, cache, io);

        string? body = await service.RefreshOneAsync(stale, CancellationToken.None);

        Assert.Null(body);
        Assert.Null(cache.Find(stale.Id));
    }

    [Fact]
    public async Task Refresh_publishes_a_new_version_when_a_part_is_added()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();
        var cache = new LivePlaylistCache();
        LivePlaylistService service = NewService(context, cache, RealIo(context));

        await service.RefreshAllAsync(CancellationToken.None);
        LivePlaylistSnapshot first = Assert.IsType<LivePlaylistSnapshot>(
            cache.Find(context.Session.Id));

        context.WritePart(
            2, PlaylistTestData.Segment("p002_00001.m4s", "2026-11-10T18:30:06Z", 6));
        await service.RefreshAllAsync(CancellationToken.None);
        LivePlaylistSnapshot second = Assert.IsType<LivePlaylistSnapshot>(
            cache.Find(context.Session.Id));

        Assert.Equal(2, second.Version);
        Assert.Contains("p001_00001.m4s", second.Body, StringComparison.Ordinal);
        Assert.Contains("p002_00001.m4s", second.Body, StringComparison.Ordinal);
        Assert.Equal(2, second.SegmentCount);
        Assert.Equal(1, SessionStoreTestContext.Occurrences(second.Body, "#EXT-X-DISCONTINUITY"));
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Refresh_failure_keeps_the_previous_snapshot()
    {
        using var context = new SessionStoreTestContext();
        await OpenAndLoadAsync(context);
        var cache = new LivePlaylistCache();
        var io = new FakeLivePlaylistIo
        {
            Handler = static (_, _) => Task.FromResult(
                LivePlaylistRead.Live(PlaylistBody("p001_00001.m4s"))),
        };
        var logs = new ListLoggerProvider();
        using ILoggerFactory loggers = TestLogging.Factory(logs);
        LivePlaylistService service = NewService(context, cache, io, loggers: loggers);

        await service.RefreshAllAsync(CancellationToken.None);
        LivePlaylistSnapshot published = Assert.IsType<LivePlaylistSnapshot>(
            cache.Find(context.Session.Id));

        io.Handler = static (_, _) =>
            Task.FromException<LivePlaylistRead>(new IOException("EIO"));
        await service.RefreshAllAsync(CancellationToken.None);

        Assert.Same(published, cache.Find(context.Session.Id));
        Assert.Contains(
            logs.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains(context.Session.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refresh_is_single_flight_per_session()
    {
        using var context = new SessionStoreTestContext();
        await OpenAndLoadAsync(context);
        var cache = new LivePlaylistCache();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new FakeLivePlaylistIo
        {
            Handler = async (_, ct) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
                return LivePlaylistRead.Live(PlaylistBody("p001_00001.m4s"));
            },
        };
        LivePlaylistService service = NewService(context, cache, io);
        RecordingSummary summary = RecordingSummary(context);

        Task first = service.RefreshOneAsync(summary, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // A busy session skips the tick instead of queueing behind the slow read.
        await service.RefreshOneAsync(summary, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, io.Calls);

        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(cache.Find(context.Session.Id));
        Assert.Equal(1, io.Calls);
    }

    [Fact]
    public async Task Complete_session_removes_the_live_entry()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();
        var cache = new LivePlaylistCache();
        LivePlaylistService service = NewService(context, cache, RealIo(context));
        await service.RefreshAllAsync(CancellationToken.None);
        Assert.NotNull(cache.Find(context.Session.Id));

        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        RecordingSummary complete = Assert.IsType<RecordingSummary>(
            context.Library.Find(context.Session.Id));
        Assert.False(complete.IsRecording);

        await service.RefreshAllAsync(CancellationToken.None);

        Assert.Null(cache.Find(context.Session.Id));
    }

    [Fact]
    public async Task Prune_removes_sessions_no_longer_in_the_library()
    {
        using var context = new SessionStoreTestContext();
        await OpenAndLoadAsync(context);
        var cache = new LivePlaylistCache();
        var io = new FakeLivePlaylistIo
        {
            Handler = static (summary, _) => Task.FromResult(
                LivePlaylistRead.Live(PlaylistBody("p001_00001.m4s"))),
        };
        LivePlaylistService service = NewService(context, cache, io);
        await service.RefreshAllAsync(CancellationToken.None);
        Assert.NotNull(cache.Find(context.Session.Id));

        RecordingSummary removed = RecordingSummary(context);
        context.Library.Remove(removed.Id);
        cache.Publish(LivePlaylistCacheTests.Snapshot("20260101T000000Z_999999999", "orphan"));

        await service.RefreshAllAsync(CancellationToken.None);

        Assert.Null(cache.Find(context.Session.Id));
        Assert.Null(cache.Find("20260101T000000Z_999999999"));
        Assert.Empty(cache.Ids);
    }

    [Fact]
    public async Task One_session_failing_does_not_stop_another_session_refreshing()
    {
        using var context = new SessionStoreTestContext();
        await OpenAndLoadAsync(context);
        RecordingSummary failing = RecordingSummary(context);
        RecordingSummary healthy = failing with
        {
            Id = "20260101T000000Z_000000002",
            StartedAt = failing.StartedAt.AddMinutes(1),
        };
        context.Library.Upsert(healthy);
        var cache = new LivePlaylistCache();
        var io = new FakeLivePlaylistIo
        {
            Handler = (summary, _) => summary.Id == failing.Id
                ? Task.FromException<LivePlaylistRead>(new IOException("EIO"))
                : Task.FromResult(LivePlaylistRead.Live(PlaylistBody("p002_00001.m4s"))),
        };
        LivePlaylistService service = NewService(context, cache, io);

        await service.RefreshAllAsync(CancellationToken.None);

        Assert.Null(cache.Find(failing.Id));
        Assert.NotNull(cache.Find(healthy.Id));
    }

    [Fact]
    public async Task Refresh_timeout_keeps_the_last_known_good_body()
    {
        using var context = new SessionStoreTestContext();
        await OpenAndLoadAsync(context);
        var cache = new LivePlaylistCache();
        var io = new FakeLivePlaylistIo
        {
            Handler = static (_, _) => Task.FromResult(
                LivePlaylistRead.Live(PlaylistBody("p001_00001.m4s"))),
        };
        var options = new LivePlaylistOptions { RefreshTimeoutSeconds = 2 };
        LivePlaylistService service = NewService(context, cache, io, options);
        await service.RefreshAllAsync(CancellationToken.None);
        LivePlaylistSnapshot published = Assert.IsType<LivePlaylistSnapshot>(
            cache.Find(context.Session.Id));

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        io.Handler = async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return LivePlaylistRead.Missing;
        };
        Task refresh = service.RefreshOneAsync(RecordingSummary(context), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        context.Clock.Advance(TimeSpan.FromSeconds(options.RefreshTimeoutSeconds));
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(published, cache.Find(context.Session.Id));
    }

    [Fact]
    public async Task Get_or_refresh_returns_the_live_body_for_a_recording()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();
        var cache = new LivePlaylistCache();
        LivePlaylistService service = NewService(context, cache, RealIo(context));

        string? playlist = await service.GetOrRefreshAsync(
            RecordingSummary(context), CancellationToken.None);

        Assert.NotNull(playlist);
        Assert.Contains("p001_00001.m4s", playlist, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-ENDLIST", playlist, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_or_refresh_returns_the_finalized_index_for_a_complete_session()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        var cache = new LivePlaylistCache();
        LivePlaylistService service = NewService(context, cache, RealIo(context));

        string? playlist = await service.GetOrRefreshAsync(
            Assert.IsType<RecordingSummary>(context.Library.Find(context.Session.Id)),
            CancellationToken.None);

        Assert.Equal(await File.ReadAllTextAsync(context.IndexPath), playlist);
        Assert.Equal(1, SessionStoreTestContext.Occurrences(playlist!, "#EXT-X-ENDLIST"));
        Assert.Null(cache.Find(context.Session.Id));
    }

    [Fact]
    public async Task Refresh_keeps_last_known_good_when_the_manifest_is_unreadable()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();
        var cache = new LivePlaylistCache();
        LivePlaylistService service = NewService(context, cache, RealIo(context));
        await service.RefreshAllAsync(CancellationToken.None);
        LivePlaylistSnapshot published = Assert.IsType<LivePlaylistSnapshot>(
            cache.Find(context.Session.Id));

        await File.WriteAllTextAsync(context.ManifestPath, "{ not json");
        await service.RefreshAllAsync(CancellationToken.None);

        Assert.Same(published, cache.Find(context.Session.Id));
    }

    [Fact]
    public async Task Get_or_refresh_returns_null_for_a_complete_session_without_an_index()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        File.Delete(context.IndexPath);
        var cache = new LivePlaylistCache();
        LivePlaylistService service = NewService(context, cache, RealIo(context));

        string? playlist = await service.GetOrRefreshAsync(
            Assert.IsType<RecordingSummary>(context.Library.Find(context.Session.Id)),
            CancellationToken.None);

        Assert.Null(playlist);
    }

    [Fact]
    public async Task Reopen_publishes_a_live_snapshot_again()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();
        var cache = new LivePlaylistCache();
        LivePlaylistService service = NewService(context, cache, RealIo(context));
        await service.RefreshAllAsync(CancellationToken.None);
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        await service.RefreshAllAsync(CancellationToken.None);
        Assert.Null(cache.Find(context.Session.Id));

        await context.Store.ReopenAsync(context.Session, CancellationToken.None);
        await service.RefreshAllAsync(CancellationToken.None);

        LivePlaylistSnapshot snapshot = Assert.IsType<LivePlaylistSnapshot>(
            cache.Find(context.Session.Id));
        Assert.Contains("p001_00001.m4s", snapshot.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-ENDLIST", snapshot.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Periodic_timer_refreshes_with_the_injected_clock()
    {
        using var context = new SessionStoreTestContext();
        await OpenAndLoadAsync(context);
        var cache = new LivePlaylistCache();
        int calls = 0;
        var firstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new FakeLivePlaylistIo
        {
            Handler = (_, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    firstCall.TrySetResult();
                if (calls == 2)
                    secondCall.TrySetResult();
                return Task.FromResult(LivePlaylistRead.Live(PlaylistBody("p001_00001.m4s")));
            },
        };
        LivePlaylistService service = NewService(context, cache, io);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await firstCall.Task.WaitAsync(TimeSpan.FromSeconds(2));
            context.Clock.Advance(TimeSpan.FromSeconds(1));
            await secondCall.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, io.Calls);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Refresh_skips_unchanged_bodies()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();
        var cache = new LivePlaylistCache();
        LivePlaylistService service = NewService(context, cache, RealIo(context));

        await service.RefreshAllAsync(CancellationToken.None);
        LivePlaylistSnapshot first = Assert.IsType<LivePlaylistSnapshot>(
            cache.Find(context.Session.Id));

        await service.RefreshAllAsync(CancellationToken.None);

        Assert.Same(first, cache.Find(context.Session.Id));
        Assert.Equal(1, first.Version);
    }

    [Fact]
    public async Task Refresh_treats_a_timeout_exception_as_a_failure()
    {
        using var context = new SessionStoreTestContext();
        await OpenAndLoadAsync(context);
        var cache = new LivePlaylistCache();
        var io = new FakeLivePlaylistIo
        {
            Handler = static (_, _) => Task.FromResult(
                LivePlaylistRead.Live(PlaylistBody("p001_00001.m4s"))),
        };
        var logs = new ListLoggerProvider();
        using ILoggerFactory loggers = TestLogging.Factory(logs);
        LivePlaylistService service = NewService(context, cache, io, loggers: loggers);
        await service.RefreshAllAsync(CancellationToken.None);
        LivePlaylistSnapshot published = Assert.IsType<LivePlaylistSnapshot>(
            cache.Find(context.Session.Id));

        io.Handler = static (_, _) =>
            Task.FromException<LivePlaylistRead>(new TimeoutException("slow"));
        await service.RefreshAllAsync(CancellationToken.None);

        Assert.Same(published, cache.Find(context.Session.Id));
        Assert.Contains(
            logs.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains(context.Session.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Get_or_refresh_returns_the_complete_body_when_a_recording_races_finalize()
    {
        using var context = new SessionStoreTestContext();
        await OpenAndLoadAsync(context);
        var cache = new LivePlaylistCache();
        string complete = PlaylistBody("p001_00001.m4s") + "#EXT-X-ENDLIST\n";
        var io = new FakeLivePlaylistIo
        {
            Handler = (_, _) => Task.FromResult(LivePlaylistRead.Complete(complete)),
        };
        LivePlaylistService service = NewService(context, cache, io);
        RecordingSummary summary = RecordingSummary(context);

        string? result = await service.GetOrRefreshAsync(summary, CancellationToken.None);

        Assert.Equal(complete, result);
        Assert.Null(cache.Find(summary.Id));
    }

    [Fact]
    public async Task Slow_session_does_not_block_another_session()
    {
        using var context = new SessionStoreTestContext();
        await OpenAndLoadAsync(context);
        RecordingSummary slow = RecordingSummary(context);
        RecordingSummary fast = slow with
        {
            Id = "20260101T000000Z_000000002",
            StartedAt = slow.StartedAt.AddMinutes(1),
        };
        context.Library.Upsert(fast);
        var cache = new LivePlaylistCache();
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var io = new FakeLivePlaylistIo
        {
            Handler = async (_, ct) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    slowStarted.TrySetResult();
                    await releaseSlow.Task.WaitAsync(ct);
                    return LivePlaylistRead.Live(PlaylistBody("p001_00001.m4s"));
                }

                fastPublished.TrySetResult();
                return LivePlaylistRead.Live(PlaylistBody("p002_00001.m4s"));
            },
        };
        LivePlaylistService service = NewService(context, cache, io);

        Task refresh = service.RefreshAllAsync(CancellationToken.None);
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fastPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(cache.Ids);

        releaseSlow.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, cache.Ids.Count);
    }

    private static LivePlaylistService NewService(
        SessionStoreTestContext context,
        LivePlaylistCache cache,
        ILivePlaylistIo io,
        LivePlaylistOptions? options = null,
        ILoggerFactory? loggers = null) =>
        new(
            context.Library,
            cache,
            io,
            Options.Create(options ?? new LivePlaylistOptions()),
            context.Clock,
            (loggers ?? NullLoggerFactory.Instance).CreateLogger<LivePlaylistService>());

    private static SessionStoreLivePlaylistIo RealIo(SessionStoreTestContext context) =>
        new(context.Store, Options.Create(new StreamKeepr.Configuration.StorageOptions
        {
            RecordingsRoot = context.Root,
        }));

    private static string PlaylistBody(string segment) =>
        PlaylistTestData.Playlist(
            6, "init_001.mp4", PlaylistTestData.Segment(segment, "2026-11-10T18:30:00Z", 6));

    private static RecordingSummary RecordingSummary(SessionStoreTestContext context) =>
        Assert.IsType<RecordingSummary>(context.Library.Find(context.Session.Id));

    private static async Task OpenAndLoadAsync(SessionStoreTestContext context)
    {
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();
    }

    private sealed class FakeLivePlaylistIo : ILivePlaylistIo
    {
        private int _calls;

        public Func<RecordingSummary, CancellationToken, Task<LivePlaylistRead>> Handler { get; set; } =
            static (_, _) => Task.FromResult(LivePlaylistRead.Missing);

        public int Calls => Volatile.Read(ref _calls);

        public Task<LivePlaylistRead> ReadAsync(RecordingSummary summary, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Handler(summary, ct);
        }
    }
}
