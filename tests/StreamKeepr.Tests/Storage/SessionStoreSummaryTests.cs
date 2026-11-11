using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

public sealed class SessionStoreSummaryTests
{
    [Fact]
    public async Task Summary_uses_the_latest_metadata_and_derives_counts_size_and_chapters()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 1.5),
            PlaylistTestData.Segment("p001_00002.m4s", "2026-11-10T16:35:01.5Z", 2.5));
        context.WriteSegment(1, 1, 10);
        context.WriteSegment(1, 2, 20);

        // A header-only part is not part of the media count or size.
        Directory.CreateDirectory(context.Directory);
        await File.WriteAllTextAsync(
            context.PartPath(2), "#EXTM3U\n#EXT-X-TARGETDURATION:6\n", CancellationToken.None);

        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        context.Clock.Advance(TimeSpan.FromSeconds(1));
        await context.Store.AppendMetadataAsync(
            context.Session, context.Metadata("Title one", "Game one"), CancellationToken.None);
        context.Clock.Advance(TimeSpan.FromSeconds(2));
        await context.Store.AppendMetadataAsync(
            context.Session, context.Metadata("Title two", "Game two"), CancellationToken.None);

        await File.WriteAllBytesAsync(context.ThumbnailPath, new byte[5], CancellationToken.None);

        var live = await context.Store.SummarizeAsync(context.Directory, CancellationToken.None);

        Assert.NotNull(live);
        Assert.Equal("Title two", live.Title);
        Assert.Equal("Game two", live.Game);
        Assert.True(live.IsRecording);
        Assert.Null(live.EndedAt);
        Assert.Null(live.DurationSeconds);
        Assert.Equal(1, live.PartCount);
        Assert.Equal(2, live.SegmentCount);
        Assert.True(live.HasThumbnail);
        Assert.Equal($"/media/streamer/{context.Session.Id}/index.m3u8", live.PlaylistUrl);
        Assert.Equal($"/media/streamer/{context.Session.Id}/thumb.jpg", live.ThumbnailUrl);

        // 3-byte init plus 30 bytes of segments; manifest, thumbnail, and part playlist excluded.
        Assert.Equal(33L, live.SizeBytes);

        Assert.Collection(
            live.Chapters,
            first =>
            {
                Assert.Equal(0, first.StartSeconds);
                Assert.Equal("Title one", first.Title);
                Assert.Equal("Game one", first.Game);
            },
            second =>
            {
                Assert.Equal(1.5, second.StartSeconds);
                Assert.Equal("Title two", second.Title);
                Assert.Equal("Game two", second.Game);
            });

        context.Clock.Advance(TimeSpan.FromMinutes(1));
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        var complete = await context.Store.SummarizeAsync(context.Directory, CancellationToken.None);

        Assert.NotNull(complete);
        Assert.False(complete.IsRecording);
        Assert.Equal(SessionStoreTestContext.Now.AddSeconds(3).AddMinutes(1), complete.EndedAt);
        Assert.Equal(4.0, complete.DurationSeconds);
        Assert.Equal(2, complete.SegmentCount);
        Assert.Equal(2, complete.Chapters.Count);
        Assert.Equal("Title two", complete.Title);
        Assert.Equal("Game two", complete.Game);
        Assert.Equal(33L + new FileInfo(context.IndexPath).Length, complete.SizeBytes);
    }

    [Fact]
    public async Task Summary_returns_null_when_the_manifest_is_missing()
    {
        using var context = new SessionStoreTestContext();

        Assert.Null(await context.Store.SummarizeAsync(context.Directory, CancellationToken.None));
    }

    [Fact]
    public async Task Summary_chapters_skip_the_initial_metadata_entry()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        var summary = await context.Store.SummarizeAsync(context.Directory, CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Empty(summary.Chapters);
    }
}
