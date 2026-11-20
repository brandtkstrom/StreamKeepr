using System.Text;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

public sealed class LivePlaylistCacheTests
{
    [Fact]
    public void Published_snapshot_is_visible_to_find()
    {
        var cache = new LivePlaylistCache();
        LivePlaylistSnapshot snapshot = Snapshot("20260101T000000Z_000000001", "one");

        cache.Publish(snapshot);

        Assert.Same(snapshot, cache.Find(snapshot.Id));
    }

    [Fact]
    public void Publishing_a_new_snapshot_replaces_the_previous_one()
    {
        var cache = new LivePlaylistCache();
        LivePlaylistSnapshot first = Snapshot("20260101T000000Z_000000001", "one");
        LivePlaylistSnapshot second = Snapshot("20260101T000000Z_000000001", "two", version: 2);

        cache.Publish(first);
        cache.Publish(second);

        Assert.Same(second, cache.Find(first.Id));
        Assert.Equal([first.Id], cache.Ids);
    }

    [Fact]
    public void Removing_a_snapshot_drops_it_from_the_cache()
    {
        var cache = new LivePlaylistCache();
        LivePlaylistSnapshot snapshot = Snapshot("20260101T000000Z_000000001", "one");
        cache.Publish(snapshot);

        cache.Remove(snapshot.Id);

        Assert.Null(cache.Find(snapshot.Id));
        Assert.Empty(cache.Ids);
    }

    [Fact]
    public async Task Concurrent_readers_never_observe_a_torn_snapshot()
    {
        var cache = new LivePlaylistCache();
        LivePlaylistSnapshot first = Snapshot("20260101T000000Z_000000001", "one");
        LivePlaylistSnapshot second = Snapshot("20260101T000000Z_000000001", "two", version: 2);
        cache.Publish(first);

        using var start = new Barrier(5);
        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < 10_000; i++)
            {
                LivePlaylistSnapshot? read = cache.Find(first.Id);
                Assert.NotNull(read);
                Assert.True(ReferenceEquals(read, first) || ReferenceEquals(read, second));
                Assert.Equal(read.Version == 1 ? first.Body : second.Body, read.Body);
            }
        })).ToArray();

        start.SignalAndWait();
        for (int i = 0; i < 10_000; i++)
            cache.Publish(i % 2 == 0 ? second : first);

        await Task.WhenAll(readers);
    }

    internal static LivePlaylistSnapshot Snapshot(
        string id, string body, long version = 1) =>
        new(
            id,
            version,
            body,
            Encoding.UTF8.GetBytes(body),
            0,
            DateTimeOffset.UnixEpoch.AddSeconds(version));
}
