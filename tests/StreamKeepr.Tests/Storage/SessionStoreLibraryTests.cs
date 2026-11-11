using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

public sealed class SessionStoreLibraryTests
{
    [Fact]
    public async Task Open_append_finalize_and_empty_delete_update_the_library_and_raise_changed()
    {
        using var context = new SessionStoreTestContext();
        int changed = 0;
        context.Library.Changed += () => changed++;

        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        Assert.Equal(1, changed);
        var opened = Assert.Single(context.Library.All);
        Assert.Equal(context.Session.Id, opened.Id);
        Assert.True(opened.IsRecording);
        Assert.Equal("A stream", opened.Title);

        context.Clock.Advance(TimeSpan.FromMinutes(1));
        await context.Store.AppendMetadataAsync(
            context.Session, context.Metadata("Changed", "New game"), CancellationToken.None);

        Assert.Equal(2, changed);
        var renamed = Assert.Single(context.Library.All);
        Assert.Equal("Changed", renamed.Title);
        Assert.Equal("New game", renamed.Game);

        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);

        Assert.Equal(3, changed);
        var complete = Assert.Single(context.Library.All);
        Assert.False(complete.IsRecording);
        Assert.NotNull(complete.DurationSeconds);

        var otherLive = new StreamKeepr.Domain.LiveStream(
            "987654321", "Other", "Other game", context.Session.StartedAt.AddHours(1), null);
        var other = StreamKeepr.Domain.Session.For(otherLive, "other", context.Root);
        await context.Store.OpenAsync(other, otherLive, CancellationToken.None);

        Assert.Equal(2, context.Library.All.Count);
        Assert.Equal(4, changed);

        await context.Store.FinalizeAsync(other, CancellationToken.None);

        Assert.Single(context.Library.All);
        Assert.Null(context.Library.Find(other.Id));
        Assert.Equal(5, changed);
    }

    [Fact]
    public async Task A_projection_failure_does_not_fail_a_manifest_mutation()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        await File.WriteAllTextAsync(
            context.PartPath(1),
            "#EXTM3U\n#EXTINF:oops,\np001_00001.m4s\n",
            CancellationToken.None);

        context.Clock.Advance(TimeSpan.FromMinutes(1));
        await context.Store.AppendMetadataAsync(
            context.Session, context.Metadata("Changed", "New game"), CancellationToken.None);

        var manifest = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Equal(2, manifest.Metadata.Count);
        Assert.Equal("Changed", manifest.Metadata[^1].Title);
    }
}
