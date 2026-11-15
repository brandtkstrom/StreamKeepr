using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Infrastructure.Emotes;

internal sealed record EmoteCatalogItem(
    string Provider, EmoteCatalogEntry Entry, EmoteImageSet Image);

internal sealed record EmoteCatalogResult(
    bool Available,
    bool Stale,
    DateTimeOffset? FetchedAt,
    IReadOnlyList<EmoteCatalogItem> Entries)
{
    public static EmoteCatalogResult Unavailable { get; } = new(false, false, null, []);
}

/// <summary>
/// Aggregates enabled provider catalogs. Global catalogs are cached once per provider for five
/// minutes with a single in-flight refresh; a failed refresh serves the stale catalog when one
/// exists and otherwise reports the catalog as unavailable.
/// </summary>
internal sealed class EmoteCatalogService(
    IEnumerable<IEmoteProvider> providers,
    IOptions<ChatOptions> options,
    TimeProvider clock,
    ILogger<EmoteCatalogService> logger)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<string, CacheSlot> _cache = new(StringComparer.Ordinal);

    public async Task<EmoteCatalogResult> GetCatalogAsync(
        EmoteChannel channel, CancellationToken ct)
    {
        if (!options.Value.Emotes.Bttv.Enabled)
            return EmoteCatalogResult.Unavailable;

        var entries = new List<EmoteCatalogItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var available = false;
        var stale = false;
        DateTimeOffset? fetchedAt = null;

        foreach (IEmoteProvider provider in providers)
        {
            GlobalCatalog? global = await GetGlobalAsync(provider, channel, ct);
            if (global is null)
                continue;

            available = true;
            stale |= global.Stale;
            if (fetchedAt is null || global.FetchedAt < fetchedAt)
                fetchedAt = global.FetchedAt;

            foreach (EmoteCatalogEntry entry in global.Entries)
            {
                if (!seen.Add(entry.Code))
                    continue;

                entries.Add(new EmoteCatalogItem(
                    provider.Id, entry, provider.GetStaticImage(entry)));
            }
        }

        return new EmoteCatalogResult(available, stale, fetchedAt, entries);
    }

    private async Task<GlobalCatalog?> GetGlobalAsync(
        IEmoteProvider provider, EmoteChannel channel, CancellationToken ct)
    {
        CacheSlot slot = _cache.GetOrAdd(provider.Id, _ => new CacheSlot());
        DateTimeOffset now = clock.GetUtcNow();
        if (slot.Cached is { } cached && now - cached.FetchedAt < CacheTtl)
            return new GlobalCatalog(cached.Entries, cached.FetchedAt, Stale: false);

        await slot.Gate.WaitAsync(ct);
        try
        {
            now = clock.GetUtcNow();
            if (slot.Cached is { } refreshed && now - refreshed.FetchedAt < CacheTtl)
                return new GlobalCatalog(refreshed.Entries, refreshed.FetchedAt, Stale: false);

            try
            {
                using var timeout = new CancellationTokenSource();
                using ITimer timer = clock.CreateTimer(
                    static state => ((CancellationTokenSource)state!).Cancel(),
                    timeout,
                    ProviderTimeout,
                    Timeout.InfiniteTimeSpan);
                using CancellationTokenSource linked =
                    CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                EmoteProviderCatalog catalog =
                    await provider.GetCatalogAsync(channel, linked.Token);
                IReadOnlyList<EmoteCatalogEntry> entries = Normalize(catalog.Global);
                DateTimeOffset fetchedAt = clock.GetUtcNow();
                slot.Cached = new CachedCatalog(entries, fetchedAt);
                return new GlobalCatalog(entries, fetchedAt, Stale: false);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                LogRefreshFailure(provider, channel, exception);
                return slot.Cached is { } stale
                    ? new GlobalCatalog(stale.Entries, stale.FetchedAt, Stale: true)
                    : null;
            }
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private void LogRefreshFailure(
        IEmoteProvider provider, EmoteChannel channel, Exception exception)
    {
        int? status = exception switch
        {
            EmoteProviderException providerError => providerError.HttpStatus,
            HttpRequestException requestError => (int?)requestError.StatusCode,
            _ => null,
        };

        logger.LogWarning(
            "Emote catalog refresh failed: provider={ProviderId}, channel={ChannelId}, status={HttpStatus}",
            provider.Id,
            channel.TwitchUserId ?? "global",
            status);
    }

    private static IReadOnlyList<EmoteCatalogEntry> Normalize(
        IEnumerable<EmoteCatalogEntry> entries) =>
        entries
            .Where(EmoteCatalogRules.IsUsable)
            .Take(EmoteCatalogRules.MaxEntriesPerCatalog)
            .ToArray();

    private sealed class CacheSlot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public CachedCatalog? Cached { get; set; }
    }

    private sealed record CachedCatalog(
        IReadOnlyList<EmoteCatalogEntry> Entries, DateTimeOffset FetchedAt);

    private sealed record GlobalCatalog(
        IReadOnlyList<EmoteCatalogEntry> Entries, DateTimeOffset FetchedAt, bool Stale);
}
