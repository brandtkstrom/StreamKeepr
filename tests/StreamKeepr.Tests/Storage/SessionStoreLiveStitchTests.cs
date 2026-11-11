using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

public sealed class SessionStoreLiveStitchTests
{
    [Fact]
    public async Task Live_stitch_strips_part_endlist_and_never_appends_one()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        string? playlist = await context.Store.StitchLiveAsync(
            context.Directory, CancellationToken.None);

        Assert.NotNull(playlist);
        Assert.Contains("#EXT-X-MAP:URI=\"init_001.mp4\"", playlist, StringComparison.Ordinal);
        Assert.Contains("p001_00001.m4s", playlist, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-ENDLIST", playlist, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', playlist);
    }

    [Fact]
    public async Task Live_stitch_cache_hit_does_not_wait_for_the_session_lock()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        _ = await context.Store.StitchLiveAsync(context.Directory, CancellationToken.None);

        var gate = context.Store.LockFor(context.Directory);
        await gate.WaitAsync();
        try
        {
            string? hit = await context.Store
                .StitchLiveAsync(context.Directory, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.NotNull(hit);
            Assert.DoesNotContain("#EXT-X-ENDLIST", hit, StringComparison.Ordinal);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task Finalize_invalidates_the_live_cache_and_complete_sessions_serve_the_index()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        string? live = await context.Store.StitchLiveAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(live);
        Assert.DoesNotContain("#EXT-X-ENDLIST", live, StringComparison.Ordinal);

        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);

        string? complete = await context.Store.StitchLiveAsync(
            context.Directory, CancellationToken.None);
        string index = await File.ReadAllTextAsync(context.IndexPath);

        Assert.Equal(index, complete);
        Assert.Equal(1, SessionStoreTestContext.Occurrences(complete!, "#EXT-X-ENDLIST"));
    }

    [Fact]
    public async Task Live_stitch_rebuilds_when_a_new_part_changes_the_cache_key()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        _ = await context.Store.StitchLiveAsync(context.Directory, CancellationToken.None);

        context.WritePart(
            2,
            PlaylistTestData.Segment("p002_00001.m4s", "2026-11-10T16:35:06Z", 6));

        string? playlist = await context.Store.StitchLiveAsync(
            context.Directory, CancellationToken.None);

        Assert.NotNull(playlist);
        Assert.Contains("p001_00001.m4s", playlist, StringComparison.Ordinal);
        Assert.Contains("p002_00001.m4s", playlist, StringComparison.Ordinal);
        Assert.Equal(1, SessionStoreTestContext.Occurrences(playlist, "#EXT-X-DISCONTINUITY"));
    }

    [Fact]
    public async Task Live_stitch_returns_null_for_an_unknown_session_or_a_complete_session_without_an_index()
    {
        using var context = new SessionStoreTestContext();
        Assert.Null(await context.Store.StitchLiveAsync(
            context.Directory, CancellationToken.None));

        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        File.Delete(context.IndexPath);

        Assert.Null(await context.Store.StitchLiveAsync(
            context.Directory, CancellationToken.None));
    }
}
