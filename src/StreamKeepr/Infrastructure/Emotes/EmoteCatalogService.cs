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
/// Aggregates enabled provider catalogs. Global catalogs are cached once per provider and
/// channel catalogs once per provider and channel ID, both for five minutes with a single
/// in-flight refresh. A failed refresh serves the cached catalog when one exists and otherwise
/// leaves that scope out; all provider work shares one three-second budget.
/// </summary>
internal sealed class EmoteCatalogService(
    IEnumerable<IEmoteProvider> providers,
    EmoteChannelResolver resolver,
    IOptions<ChatOptions> options,
    TimeProvider clock,
    ILogger<EmoteCatalogService> logger)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<string, CacheSlot> _globalCatalogs =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, CacheSlot> _channelCatalogs =
        new(StringComparer.Ordinal);

    public async Task<EmoteCatalogResult> GetCatalogAsync(
        EmoteChannel channel, CancellationToken ct)
    {
        if (!options.Value.Emotes.Bttv.Enabled)
            return EmoteCatalogResult.Unavailable;

        using var budget = new CancellationTokenSource();
        using ITimer timer = clock.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            budget,
            ProviderTimeout,
            Timeout.InfiniteTimeSpan);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);

        string? channelId = EmoteCatalogRules.IsValidChannelId(channel.TwitchUserId)
            ? channel.TwitchUserId
            : null;
        if (channelId is null && !string.IsNullOrWhiteSpace(channel.Login))
        {
            try
            {
                channelId = await resolver.ResolveAsync(channel.Login, linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The shared three-second budget expired; serve the global catalog only.
            }
        }

        var resolved = channel with { TwitchUserId = channelId };
        var entries = new List<EmoteCatalogItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var available = false;
        var stale = false;
        DateTimeOffset? fetchedAt = null;

        foreach (IEmoteProvider provider in providers)
        {
            ProviderCatalog? catalog = await GetProviderCatalogAsync(
                provider, resolved, linked.Token, ct);
            if (catalog is null)
                continue;

            available = true;
            stale |= catalog.Stale;
            if (fetchedAt is null || catalog.FetchedAt < fetchedAt)
                fetchedAt = catalog.FetchedAt;

            foreach (EmoteCatalogEntry entry in catalog.Entries)
            {
                if (!seen.Add(entry.Code))
                    continue;

                entries.Add(new EmoteCatalogItem(
                    provider.Id, entry, provider.GetStaticImage(entry)));
            }
        }

        return new EmoteCatalogResult(available, stale, fetchedAt, entries);
    }

    private async Task<ProviderCatalog?> GetProviderCatalogAsync(
        IEmoteProvider provider,
        EmoteChannel channel,
        CancellationToken work,
        CancellationToken callerCt)
    {
        CacheSlot globalSlot = _globalCatalogs.GetOrAdd(provider.Id, _ => new CacheSlot());
        CacheSlot? channelSlot = EmoteCatalogRules.IsValidChannelId(channel.TwitchUserId)
            ? _channelCatalogs.GetOrAdd(
                $"{provider.Id}:{channel.TwitchUserId}", _ => new CacheSlot())
            : null;
        CacheSlot gate = channelSlot ?? globalSlot;

        // The shared budget may have expired during channel-ID resolution.
        if (work.IsCancellationRequested)
            return Read(globalSlot, channelSlot, clock.GetUtcNow());

        DateTimeOffset now = clock.GetUtcNow();
        if (IsFresh(globalSlot, now) && (channelSlot is null || IsFresh(channelSlot, now)))
            return Read(globalSlot, channelSlot, now);

        bool acquired = false;
        try
        {
            try
            {
                await gate.Gate.WaitAsync(work);
                acquired = true;
            }
            catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
            {
                return Read(globalSlot, channelSlot, clock.GetUtcNow());
            }

            now = clock.GetUtcNow();
            if (IsFresh(globalSlot, now) && (channelSlot is null || IsFresh(channelSlot, now)))
                return Read(globalSlot, channelSlot, now);

            try
            {
                EmoteProviderCatalog catalog = await provider.GetCatalogAsync(channel, work);
                DateTimeOffset fetchedAt = clock.GetUtcNow();
                globalSlot.Publish(Normalize(catalog.Global), fetchedAt);
                channelSlot?.Publish(Normalize(catalog.Channel), fetchedAt);
                return Read(globalSlot, channelSlot, fetchedAt);
            }
            catch (Exception exception) when (!callerCt.IsCancellationRequested)
            {
                LogRefreshFailure(provider, channel, exception);
                return Read(globalSlot, channelSlot, clock.GetUtcNow());
            }
        }
        finally
        {
            if (acquired)
                gate.Gate.Release();
        }
    }

    /// <summary>Channel entries precede global entries; first entry wins the service-level dedupe.</summary>
    private static ProviderCatalog? Read(
        CacheSlot global, CacheSlot? channel, DateTimeOffset now)
    {
        CachedCatalog? globalCache = global.Cached;
        CachedCatalog? channelCache = channel?.Cached;
        if (globalCache is null && channelCache is null)
            return null;

        var entries = new List<EmoteCatalogEntry>(
            (channelCache?.Entries.Count ?? 0) + (globalCache?.Entries.Count ?? 0));
        var stale = false;
        DateTimeOffset fetchedAt = now;

        if (channelCache is not null)
        {
            entries.AddRange(channelCache.Entries);
            stale |= now - channelCache.FetchedAt >= CacheTtl;
            fetchedAt = channelCache.FetchedAt;
        }

        if (globalCache is not null)
        {
            entries.AddRange(globalCache.Entries);
            stale |= now - globalCache.FetchedAt >= CacheTtl;
            if (globalCache.FetchedAt < fetchedAt)
                fetchedAt = globalCache.FetchedAt;
        }

        return new ProviderCatalog(entries, fetchedAt, stale);
    }

    private static bool IsFresh(CacheSlot slot, DateTimeOffset now) =>
        slot.Cached is { } cached && now - cached.FetchedAt < CacheTtl;

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
        private readonly object _sync = new();
        private CachedCatalog? _cached;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public CachedCatalog? Cached => Volatile.Read(ref _cached);

        public void Publish(IReadOnlyList<EmoteCatalogEntry> entries, DateTimeOffset fetchedAt)
        {
            lock (_sync)
            {
                if (_cached is null || fetchedAt >= _cached.FetchedAt)
                    Volatile.Write(ref _cached, new CachedCatalog(entries, fetchedAt));
            }
        }
    }

    private sealed record CachedCatalog(
        IReadOnlyList<EmoteCatalogEntry> Entries, DateTimeOffset FetchedAt);

    private sealed record ProviderCatalog(
        IReadOnlyList<EmoteCatalogEntry> Entries, DateTimeOffset FetchedAt, bool Stale);
}
