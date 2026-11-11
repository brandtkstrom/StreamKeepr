using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

public sealed class SessionStoreFinalizeTests
{
    [Fact]
    public async Task Finalize_stitches_parts_in_numeric_order_with_one_endlist()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            2,
            PlaylistTestData.Segment("p002_00001.m4s", "2026-11-10T16:35:03Z", 2.5));
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 1.5));
        context.WriteSegment(1, 1, 10);
        context.WriteSegment(2, 1, 20);

        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Clock.Advance(TimeSpan.FromMinutes(2));
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);

        string index = await File.ReadAllTextAsync(context.IndexPath);
        Assert.StartsWith("#EXTM3U\n", index, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-TARGETDURATION:12", index, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MAP:URI=\"init_001.mp4\"", index, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MAP:URI=\"init_002.mp4\"", index, StringComparison.Ordinal);
        Assert.Equal(1, SessionStoreTestContext.Occurrences(index, "#EXT-X-DISCONTINUITY"));
        Assert.Equal(1, SessionStoreTestContext.Occurrences(index, "#EXT-X-ENDLIST"));
        Assert.EndsWith("#EXT-X-ENDLIST\n", index, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', index);
        Assert.True(
            index.IndexOf("p001_00001.m4s", StringComparison.Ordinal)
            < index.IndexOf("p002_00001.m4s", StringComparison.Ordinal));

        var manifest = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Equal(SessionStatus.Complete, manifest.Status);
        Assert.Equal(SessionStoreTestContext.Now.AddMinutes(2), manifest.EndedAt);
        Assert.Equal(4.0, manifest.DurationSeconds);
        Assert.Equal(3 + 3 + 10 + 20 + new FileInfo(context.IndexPath).Length, manifest.SizeBytes);

        Assert.False(File.Exists(context.ManifestPath + ".tmp"));
        Assert.False(File.Exists(context.IndexPath + ".tmp"));
    }

    [Fact]
    public async Task Finalize_deletes_a_session_that_never_produced_media()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        // A header-only part playlist has no segment URI and does not count as media.
        await File.WriteAllTextAsync(
            context.PartPath(1), "#EXTM3U\n#EXT-X-TARGETDURATION:6\n", CancellationToken.None);

        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);

        Assert.False(Directory.Exists(context.Directory));
        Assert.Null(await context.Store.TryLoadAsync(context.Directory, CancellationToken.None));
        Assert.Null(await context.Store.SummarizeAsync(context.Directory, CancellationToken.None));
        Assert.Null(await context.Store.StitchLiveAsync(context.Directory, CancellationToken.None));
    }

    [Fact]
    public async Task AppendMetadata_records_changes_and_skips_repeated_observations()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        await context.Store.AppendMetadataAsync(
            context.Session, context.Metadata(), CancellationToken.None);

        var unchanged = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(unchanged);
        Assert.Single(unchanged.Metadata);

        context.Clock.Advance(TimeSpan.FromMinutes(1));
        await context.Store.AppendMetadataAsync(
            context.Session, context.Metadata("Title two", "Game two"), CancellationToken.None);
        await context.Store.AppendMetadataAsync(
            context.Session, context.Metadata("Title two", "Game two"), CancellationToken.None);
        await context.Store.AppendMetadataAsync(
            context.Session, context.Metadata("Title two", "Game three"), CancellationToken.None);

        var manifest = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Collection(
            manifest.Metadata,
            first => Assert.Equal("A stream", first.Title),
            second =>
            {
                Assert.Equal("Title two", second.Title);
                Assert.Equal("Game two", second.Game);
                Assert.Equal(SessionStoreTestContext.Now.AddMinutes(1), second.At);
            },
            third =>
            {
                Assert.Equal("Title two", third.Title);
                Assert.Equal("Game three", third.Game);
            });
    }

    [Fact]
    public async Task Finalize_preserves_the_original_end_time_when_refinalized()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        context.Clock.Advance(TimeSpan.FromMinutes(1));
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        var first = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);

        context.Clock.Advance(TimeSpan.FromMinutes(10));
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        var second = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.EndedAt, second.EndedAt);
        Assert.Equal(SessionStatus.Complete, second.Status);
        Assert.Equal(
            1,
            SessionStoreTestContext.Occurrences(
                await File.ReadAllTextAsync(context.IndexPath), "#EXT-X-ENDLIST"));
    }

    [Fact]
    public async Task A_failed_index_write_leaves_the_previous_index_and_manifest()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        string previous = await File.ReadAllTextAsync(context.IndexPath);

        context.WritePart(
            2,
            PlaylistTestData.Segment("p002_00001.m4s", "2026-11-10T16:35:06Z", 6));
        Directory.CreateDirectory(context.IndexPath + ".tmp");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Store.FinalizeAsync(context.Session, CancellationToken.None));

        Assert.Equal(previous, await File.ReadAllTextAsync(context.IndexPath));
        var manifest = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Equal(SessionStatus.Complete, manifest.Status);
    }

    [Fact]
    public async Task Finalize_ignores_a_part_playlist_outside_the_1_to_999_range()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        await File.WriteAllTextAsync(
            context.PartPath(1000),
            PlaylistTestData.Playlist(
                6,
                "init_1000.mp4",
                PlaylistTestData.Segment("p1000_00001.m4s", "2026-11-10T16:35:06Z", 6)),
            CancellationToken.None);

        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);

        string index = await File.ReadAllTextAsync(context.IndexPath);
        Assert.Contains("p001_00001.m4s", index, StringComparison.Ordinal);
        Assert.DoesNotContain("p1000_00001.m4s", index, StringComparison.Ordinal);
        Assert.Equal(1, SessionStoreTestContext.Occurrences(index, "#EXT-X-ENDLIST"));

        var manifest = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Equal(SessionStatus.Complete, manifest.Status);
    }
}
