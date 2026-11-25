using System.Collections.Concurrent;
using StreamKeepr.Infrastructure.Emotes;

namespace StreamKeepr.Tests.Emotes;

public sealed class BoundedEmoteCacheTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    [Fact]
    public void A_cache_at_or_below_the_limit_is_untouched()
    {
        ConcurrentDictionary<string, SingleFlightSlot<Entry>> cache =
            CreateCache(("a", Now), ("b", Now));

        BoundedEmoteCache.Trim(cache, limit: 2, Now, Ttl, entry => entry.FetchedAt);

        Assert.Equal(["a", "b"], cache.Keys.Order());
    }

    [Fact]
    public void Expired_entries_are_pruned_before_fresh_entries()
    {
        ConcurrentDictionary<string, SingleFlightSlot<Entry>> cache = CreateCache(
            ("fresh", Now),
            ("expired", Now.AddMinutes(-6)),
            ("empty", null));

        BoundedEmoteCache.Trim(cache, limit: 2, Now, Ttl, entry => entry.FetchedAt);

        Assert.Equal(["fresh"], cache.Keys);
    }

    [Fact]
    public void Protected_in_flight_entries_are_not_pruned()
    {
        ConcurrentDictionary<string, SingleFlightSlot<Entry>> cache =
            CreateCache(("done", Now), ("inflight", null));

        BoundedEmoteCache.Trim(
            cache,
            limit: 0,
            Now,
            Ttl,
            entry => entry.FetchedAt,
            slot => slot.Value is not null);

        Assert.True(cache.ContainsKey("inflight"));
        Assert.False(cache.ContainsKey("done"));
    }

    [Fact]
    public void Fresh_entries_are_pruned_when_every_entry_is_fresh()
    {
        ConcurrentDictionary<string, SingleFlightSlot<Entry>> cache =
            CreateCache(("a", Now), ("b", Now), ("c", Now));

        BoundedEmoteCache.Trim(cache, limit: 2, Now, Ttl, entry => entry.FetchedAt);

        Assert.Equal(2, cache.Count);
    }

    private static ConcurrentDictionary<string, SingleFlightSlot<Entry>> CreateCache(
        params (string Key, DateTimeOffset? FetchedAt)[] entries)
    {
        var cache = new ConcurrentDictionary<string, SingleFlightSlot<Entry>>(
            StringComparer.Ordinal);
        foreach ((string key, DateTimeOffset? fetchedAt) in entries)
        {
            cache[key] = new SingleFlightSlot<Entry>
            {
                Value = fetchedAt is { } value ? new Entry(value) : null,
            };
        }

        return cache;
    }

    private sealed record Entry(DateTimeOffset FetchedAt);
}
