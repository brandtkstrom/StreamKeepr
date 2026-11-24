using System.Collections.Concurrent;

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
/// Aggregates enabled provider catalogs. Global refreshes start before channel-ID resolution so
/// a slow resolver cannot keep a cold global catalog from being fetched inside the shared
/// three-second budget; channel scopes start once an ID is known. Global catalogs are cached
/// once per provider and channel catalogs once per provider and channel ID, both for five
/// minutes with a single in-flight refresh per key. A failed refresh serves that scope's cached
/// catalog when one exists and otherwise leaves the scope out.
/// </summary>
internal sealed class EmoteCatalogService(
    IEnumerable<IEmoteProvider> providers,
    IEmoteChannelResolver resolver,
    TimeProvider clock,
    ILogger<EmoteCatalogService> logger)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(3);

    // The global cache is bounded by the provider count; channel entries get an explicit cap.
    private const int ChannelCacheLimit = 128;

    private readonly ConcurrentDictionary<string, SingleFlightSlot<CatalogScope>> _globalCatalogs =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, SingleFlightSlot<CatalogScope>> _channelCatalogs =
        new(StringComparer.Ordinal);

    public async Task<EmoteCatalogResult> GetCatalogAsync(string login, CancellationToken ct)
    {
        // Enablement is provider-specific so a future provider needs no change here.
        IEmoteProvider[] enabled = providers.Where(provider => provider.Enabled).ToArray();
        if (enabled.Length == 0)
            return EmoteCatalogResult.Unavailable;

        using var budget = new CancellationTokenSource();
        using ITimer timer = clock.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            budget,
            ProviderTimeout,
            Timeout.InfiniteTimeSpan);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);

        // Start every global refresh before resolution. A slow resolver then cannot consume the
        // budget before a cold global catalog has started fetching (REQ-012).
        Task<CatalogScope?>[] globalTasks = enabled
            .Select(provider => RefreshGlobalAsync(provider, linked.Token, ct))
            .ToArray();

        string? channelId;
        try
        {
            channelId = await ResolveChannelAsync(login, linked.Token, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller left. Observe the refreshes already in flight, then propagate.
            await Task.WhenAll(globalTasks).ConfigureAwait(false);
            throw;
        }

        Task<CatalogScope?>[] channelTasks = channelId is null
            ? []
            : enabled
                .Select(provider => RefreshChannelAsync(provider, channelId, linked.Token, ct))
                .ToArray();

        Task<CatalogScope?[]> globalResults = Task.WhenAll(globalTasks);
        Task<CatalogScope?[]> channelResults = Task.WhenAll(channelTasks);

        // Wait for both sets before reading either result, so caller cancellation never leaves
        // a started refresh unobserved.
        await Task.WhenAll(globalResults, channelResults).ConfigureAwait(false);
        CatalogScope?[] globals = await globalResults.ConfigureAwait(false);
        CatalogScope?[] channels = await channelResults.ConfigureAwait(false);

        var entries = new List<EmoteCatalogItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var providerScopes = new List<CatalogScope>(enabled.Length);

        for (int index = 0; index < enabled.Length; index++)
        {
            CatalogScope? scope = Combine([
                channelId is null ? null : channels[index],
                globals[index],
            ]);
            if (scope is null)
                continue;

            providerScopes.Add(scope);
            IEmoteProvider provider = enabled[index];
            foreach (EmoteCatalogEntry entry in scope.Entries)
            {
                if (!seen.Add(entry.Code))
                    continue;

                entries.Add(new EmoteCatalogItem(
                    provider.Id, entry, provider.GetImageSet(entry)));
            }
        }

        return Combine(providerScopes) is { } catalog
            ? new EmoteCatalogResult(true, catalog.Stale, catalog.FetchedAt, entries)
            : EmoteCatalogResult.Unavailable;
    }

    private async Task<string?> ResolveChannelAsync(
        string login, CancellationToken work, CancellationToken callerCt)
    {
        if (string.IsNullOrWhiteSpace(login))
            return null;

        try
        {
            return await resolver.ResolveAsync(login, work);
        }
        catch (OperationCanceledException exception) when (!callerCt.IsCancellationRequested)
        {
            logger.LogWarning(
                "Emote channel ID resolution timed out for {Login}; serving global emotes only: {Reason}",
                login,
                EmoteFailure.Describe(exception).Reason);
            return null;
        }
    }

    private Task<CatalogScope?> RefreshGlobalAsync(
        IEmoteProvider provider, CancellationToken work, CancellationToken callerCt)
    {
        SingleFlightSlot<CatalogScope> slot = _globalCatalogs.GetOrAdd(
            provider.Id, static _ => new SingleFlightSlot<CatalogScope>());

        return RefreshScopeAsync(
            provider,
            slot,
            scope: "global",
            channelId: null,
            work,
            callerCt,
            token => provider.GetGlobalAsync(token));
    }

    private Task<CatalogScope?> RefreshChannelAsync(
        IEmoteProvider provider,
        string channelId,
        CancellationToken work,
        CancellationToken callerCt)
    {
        string key = $"{provider.Id}:{channelId}";
        if (!_channelCatalogs.TryGetValue(key, out SingleFlightSlot<CatalogScope>? slot))
        {
            BoundedEmoteCache.Trim(
                _channelCatalogs,
                ChannelCacheLimit,
                clock.GetUtcNow(),
                CacheTtl,
                static cached => cached.FetchedAt);
            slot = _channelCatalogs.GetOrAdd(
                key, static _ => new SingleFlightSlot<CatalogScope>());
        }

        return RefreshScopeAsync(
            provider,
            slot,
            scope: "channel",
            channelId,
            work,
            callerCt,
            token => provider.GetChannelAsync(channelId, token));
    }

    /// <summary>
    /// Returns the scope's fresh cache, refreshes it under its single-flight gate, or falls back
    /// to its stale cache. A failure with no cache leaves the scope out. <see cref="CatalogScope.Stale"/>
    /// is decided here, when the scope is read, so a later scope's fetch cannot retroactively
    /// make an earlier fresh catalog stale.
    /// </summary>
    private async Task<CatalogScope?> RefreshScopeAsync(
        IEmoteProvider provider,
        SingleFlightSlot<CatalogScope> slot,
        string scope,
        string? channelId,
        CancellationToken work,
        CancellationToken callerCt,
        Func<CancellationToken, Task<IReadOnlyList<EmoteCatalogEntry>>> fetch)
    {
        // The shared budget may have expired during channel-ID resolution or the other scope.
        if (work.IsCancellationRequested)
            return Served(slot.Value, clock.GetUtcNow());

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
                return Served(slot.Value, clock.GetUtcNow());
            }

            now = clock.GetUtcNow();
            if (IsFresh(slot.Value, now))
                return slot.Value;

            try
            {
                IReadOnlyList<EmoteCatalogEntry> entries = await fetch(work);
                DateTimeOffset fetchedAt = clock.GetUtcNow();
                var cached = new CatalogScope(Normalize(entries), fetchedAt);
                slot.Value = cached;
                return cached;
            }
            catch (Exception exception) when (!callerCt.IsCancellationRequested)
            {
                LogRefreshFailure(provider, scope, channelId, exception);
                return Served(slot.Value, clock.GetUtcNow());
            }
        }
        finally
        {
            if (acquired)
                slot.Gate.Release();
        }
    }

    /// <summary>
    /// Rolls up scopes in order: channel before global within a provider, and providers in
    /// registration order. The caller supplies the order; this helper only concatenates entries,
    /// takes the earliest fetch time, and ORs the stale flags decided at scope-read time.
    /// </summary>
    private static CatalogScope? Combine(IEnumerable<CatalogScope?> scopes)
    {
        CatalogScope[] available = scopes
            .Where(scope => scope is not null)
            .Select(scope => scope!)
            .ToArray();
        if (available.Length == 0)
            return null;

        return new CatalogScope(
            [.. available.SelectMany(scope => scope.Entries)],
            available.Min(scope => scope.FetchedAt),
            available.Any(scope => scope.Stale));
    }

    /// <summary>Marks a cached scope stale when it is older than the TTL at the moment it is read.</summary>
    private static CatalogScope? Served(CatalogScope? cached, DateTimeOffset now) =>
        cached is null
            ? null
            : cached with { Stale = !IsFresh(cached, now) };

    private static bool IsFresh(CatalogScope? cached, DateTimeOffset now) =>
        cached is not null && now - cached.FetchedAt < CacheTtl;

    private void LogRefreshFailure(
        IEmoteProvider provider, string scope, string? channelId, Exception exception)
    {
        (int? httpStatus, string reason) = EmoteFailure.Describe(exception);

        // ChannelId and HttpStatus stay null rather than "global"/"none" so sinks can filter
        // on real IDs and numeric statuses; Scope already says which catalog failed.
        logger.LogWarning(
            exception,
            "Emote catalog refresh failed for {ProviderId} {Scope} emotes " +
            "(channel {ChannelId}, HTTP {HttpStatus}): {Reason}",
            provider.Id,
            scope,
            channelId,
            httpStatus,
            reason);
    }

    private static IReadOnlyList<EmoteCatalogEntry> Normalize(
        IEnumerable<EmoteCatalogEntry> entries) =>
        entries
            .Where(EmoteCatalogRules.IsUsable)
            .Take(EmoteCatalogRules.MaxEntriesPerCatalog)
            .ToArray();

    private sealed record CatalogScope(
        IReadOnlyList<EmoteCatalogEntry> Entries, DateTimeOffset FetchedAt, bool Stale = false);
}
