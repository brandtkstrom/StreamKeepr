using System.Collections.Concurrent;
using System.Globalization;
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

    private readonly ConcurrentDictionary<string, SingleFlightSlot<CachedCatalog>> _globalCatalogs =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, SingleFlightSlot<CachedCatalog>> _channelCatalogs =
        new(StringComparer.Ordinal);

    public async Task<EmoteCatalogResult> GetCatalogAsync(
        EmoteChannel channel, CancellationToken ct)
    {
        if (!options.Value.Emotes.AnyEnabled)
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
                logger.LogWarning(
                    "Emote channel ID resolution timed out; serving global emotes only: " +
                    "channel={ChannelId}, login={Login}",
                    channel.TwitchUserId ?? "global",
                    channel.Login);
            }
        }

        var entries = new List<EmoteCatalogItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var available = false;
        var stale = false;
        DateTimeOffset? fetchedAt = null;

        foreach (IEmoteProvider provider in providers)
        {
            // Enablement is provider-specific so a future provider needs no change here.
            if (!provider.Enabled)
                continue;

            ProviderCatalog? catalog = await GetProviderCatalogAsync(
                provider, channelId, linked.Token, ct);
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

    /// <summary>
    /// Refreshes the provider's global and channel scopes independently. Each scope owns its
    /// cache key, TTL, stale fallback, and single-flight gate, so one scope's failure neither
    /// discards the other's catalog nor blocks its refresh.
    /// </summary>
    private async Task<ProviderCatalog?> GetProviderCatalogAsync(
        IEmoteProvider provider,
        string? channelId,
        CancellationToken work,
        CancellationToken callerCt)
    {
        SingleFlightSlot<CachedCatalog> globalSlot = _globalCatalogs.GetOrAdd(
            provider.Id, static _ => new SingleFlightSlot<CachedCatalog>());

        CachedCatalog? global = await RefreshScopeAsync(
            provider,
            globalSlot,
            scope: "global",
            channelId,
            work,
            callerCt,
            token => provider.GetGlobalAsync(token));

        CachedCatalog? channel = null;
        if (channelId is not null)
        {
            string resolvedId = channelId;
            SingleFlightSlot<CachedCatalog> channelSlot = _channelCatalogs.GetOrAdd(
                $"{provider.Id}:{resolvedId}", static _ => new SingleFlightSlot<CachedCatalog>());
            channel = await RefreshScopeAsync(
                provider,
                channelSlot,
                scope: "channel",
                resolvedId,
                work,
                callerCt,
                token => provider.GetChannelAsync(resolvedId, token));
        }

        return Combine(channel, global, clock.GetUtcNow());
    }

    /// <summary>
    /// Returns the scope's fresh cache, refreshes it under its single-flight gate, or falls back
    /// to its stale cache. A failure with no cache leaves the scope out.
    /// </summary>
    private async Task<CachedCatalog?> RefreshScopeAsync(
        IEmoteProvider provider,
        SingleFlightSlot<CachedCatalog> slot,
        string scope,
        string? channelId,
        CancellationToken work,
        CancellationToken callerCt,
        Func<CancellationToken, Task<IReadOnlyList<EmoteCatalogEntry>>> fetch)
    {
        // The shared budget may have expired during channel-ID resolution or the other scope.
        if (work.IsCancellationRequested)
            return slot.Value;

        DateTimeOffset now = clock.GetUtcNow();
        if (IsFresh(slot.Value, now))
            return slot.Value;

        bool acquired = false;
        try
        {
            try
            {
                await slot.Gate.WaitAsync(work);
                acquired = true;
            }
            catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
            {
                return slot.Value;
            }

            now = clock.GetUtcNow();
            if (IsFresh(slot.Value, now))
                return slot.Value;

            try
            {
                IReadOnlyList<EmoteCatalogEntry> entries = await fetch(work);
                DateTimeOffset fetchedAt = clock.GetUtcNow();
                var cached = new CachedCatalog(Normalize(entries), fetchedAt);
                slot.Value = cached;
                return cached;
            }
            catch (Exception exception) when (!callerCt.IsCancellationRequested)
            {
                LogRefreshFailure(provider, scope, channelId, exception);
                return slot.Value;
            }
        }
        finally
        {
            if (acquired)
                slot.Gate.Release();
        }
    }

    /// <summary>Channel entries precede global entries; first entry wins the service-level dedupe.</summary>
    private static ProviderCatalog? Combine(
        CachedCatalog? channel, CachedCatalog? global, DateTimeOffset now)
    {
        if (global is null && channel is null)
            return null;

        var entries = new List<EmoteCatalogEntry>(
            (channel?.Entries.Count ?? 0) + (global?.Entries.Count ?? 0));
        var stale = false;
        DateTimeOffset? fetchedAt = null;

        if (channel is not null)
        {
            entries.AddRange(channel.Entries);
            stale |= !IsFresh(channel, now);
            fetchedAt = channel.FetchedAt;
        }

        if (global is not null)
        {
            entries.AddRange(global.Entries);
            stale |= !IsFresh(global, now);
            if (fetchedAt is null || global.FetchedAt < fetchedAt)
                fetchedAt = global.FetchedAt;
        }

        return new ProviderCatalog(entries, fetchedAt!.Value, stale);
    }

    private static bool IsFresh(CachedCatalog? cached, DateTimeOffset now) =>
        cached is not null && now - cached.FetchedAt < CacheTtl;

    private void LogRefreshFailure(
        IEmoteProvider provider, string scope, string? channelId, Exception exception)
    {
        int? httpStatus = exception switch
        {
            EmoteProviderException providerError => providerError.HttpStatus,
            HttpRequestException requestError => (int?)requestError.StatusCode,
            _ => null,
        };
        string reason = exception switch
        {
            EmoteProviderException { Kind: EmoteProviderFailureKind.InvalidPayload } =>
                "invalid-payload",
            EmoteProviderException { Kind: EmoteProviderFailureKind.TooLarge } => "too-large",
            EmoteProviderException => "http",
            OperationCanceledException => "timeout",
            HttpRequestException => "transport",
            _ => "error",
        };
        string status = httpStatus?.ToString(CultureInfo.InvariantCulture) ?? "none";

        logger.LogWarning(
            exception,
            "Emote catalog refresh failed: provider={ProviderId}, scope={Scope}, " +
            "channel={ChannelId}, status={HttpStatus}, reason={Reason}",
            provider.Id,
            scope,
            channelId ?? "global",
            status,
            reason);
    }

    private static IReadOnlyList<EmoteCatalogEntry> Normalize(
        IEnumerable<EmoteCatalogEntry> entries) =>
        entries
            .Where(EmoteCatalogRules.IsUsable)
            .Take(EmoteCatalogRules.MaxEntriesPerCatalog)
            .ToArray();

    private sealed record CachedCatalog(
        IReadOnlyList<EmoteCatalogEntry> Entries, DateTimeOffset FetchedAt);

    private sealed record ProviderCatalog(
        IReadOnlyList<EmoteCatalogEntry> Entries, DateTimeOffset FetchedAt, bool Stale);
}
