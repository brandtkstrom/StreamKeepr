using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

public sealed class SessionStoreConcurrencyTests
{
    [Fact]
    public async Task Concurrent_finalize_append_stitch_and_summarize_do_not_corrupt_the_session()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        context.WritePart(
            2,
            PlaylistTestData.Segment("p002_00001.m4s", "2026-11-10T16:35:06Z", 6));
        context.WriteSegment(1, 1, 10);
        context.WriteSegment(2, 1, 20);
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        var operations = new List<Task>(61)
        {
            context.Store.FinalizeAsync(context.Session, CancellationToken.None),
        };

        for (int i = 0; i < 25; i++)
        {
            int title = i;
            operations.Add(context.Store.AppendMetadataAsync(
                context.Session,
                context.Metadata($"Title {title}", $"Game {title}"),
                CancellationToken.None));
            operations.Add(context.Store.StitchLiveAsync(
                context.Directory, CancellationToken.None));
        }

        for (int i = 0; i < 10; i++)
            operations.Add(context.Store.SummarizeAsync(context.Directory, CancellationToken.None));

        await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(15));

        var manifest = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Equal(SessionStatus.Complete, manifest.Status);
        Assert.NotEmpty(manifest.Metadata);
        Assert.All(manifest.Metadata, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Title));
            Assert.False(string.IsNullOrWhiteSpace(entry.Game));
        });

        string index = await File.ReadAllTextAsync(context.IndexPath);
        Assert.Equal(1, SessionStoreTestContext.Occurrences(index, "#EXT-X-ENDLIST"));
        Assert.Equal(1, SessionStoreTestContext.Occurrences(index, "#EXT-X-DISCONTINUITY"));
        Assert.Equal(12.0, PlaylistStitcher.TotalDuration(index));

        Assert.False(File.Exists(context.ManifestPath + ".tmp"));
        Assert.False(File.Exists(context.IndexPath + ".tmp"));
        Assert.Equal(1, context.Store.LockFor(context.Directory).CurrentCount);
    }

    [Fact]
    public async Task Two_sessions_use_independent_locks()
    {
        using var context = new SessionStoreTestContext();
        var otherLive = new StreamKeepr.Domain.LiveStream(
            "987654321", "Other", "Other game", context.Session.StartedAt, null);
        var other = StreamKeepr.Domain.Session.For(otherLive, "other", context.Root);

        var firstGate = context.Store.LockFor(context.Session.Directory);
        var secondGate = context.Store.LockFor(other.Directory);

        Assert.NotSame(firstGate, secondGate);
        await firstGate.WaitAsync();
        try
        {
            Assert.True(await secondGate.WaitAsync(TimeSpan.FromSeconds(1)));
            secondGate.Release();
        }
        finally
        {
            firstGate.Release();
        }
    }
}
