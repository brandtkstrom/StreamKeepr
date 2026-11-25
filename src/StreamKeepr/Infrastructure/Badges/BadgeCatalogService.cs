using System.Collections.Concurrent;
using StreamKeepr.Infrastructure.Emotes;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Infrastructure.Badges;

/// <summary>The badge catalog scopes and the freshness of the copies that served them.</summary>
internal sealed record BadgeCatalogResult(
    bool Available,
    bool Stale,
    DateTimeOffset? FetchedAt,
    IReadOnlyList<BadgeCatalogEntry> Global,
    IReadOnlyList<BadgeCatalogEntry> Channel)
{
    public static BadgeCatalogResult Unavailable { get; } = new(false, false, null, [], []);
}

/// <summary>
/// Caches Twitch's global badge catalog once and each channel's catalog by broadcaster ID for an
/// hour. Every cache key has its own in-flight refresh, stale fallback, and failure boundary, so
/// one scope failing never discards the other's catalog. A failure with no cache leaves that scope
/// empty and never surfaces as an error. Badge failures never affect chat, emotes, capture,
/// recording, or health. The cache semantics deliberately mirror <c>EmoteCatalogService</c>
/// without sharing its provider seam: badges are not emotes (ADR-0011).
/// </summary>
internal sealed class BadgeCatalogService(
    IBadgeSource source,
    IEmoteChannelResolver resolver,
    TimeProvider clock,
    ILogger<BadgeCatalogService> logger)
{
    /// <summary>Badges change rarely; an hour keeps the app off Helix without visible drift.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(60);

    /// <summary>
    /// The global catalog and channel-ID resolution share this budget; each channel refresh has
    /// its own budget of the same length.
    /// </summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(3);

    /// <summary>One channel catalog is small; this only bounds an unbounded recording library.</summary>
    private const int ChannelCacheLimit = 256;

    private static readonly IReadOnlyList<BadgeCatalogEntry> NoEntries = [];

    private readonly SingleFlightSlot<BadgeScope> _global = new();

    private readonly ConcurrentDictionary<string, SingleFlightSlot<BadgeScope>> _channels =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the global and channel scopes for a recording's channel login. A blank or
    /// unresolved login is not an error: the result carries the global catalog and no channel one.
    /// </summary>
    public async Task<BadgeCatalogResult> GetCatalogAsync(string login, CancellationToken ct)
    {
        using var budget = new CancellationTokenSource();
        using ITimer timer = clock.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            budget,
            FetchTimeout,
            Timeout.InfiniteTimeSpan);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);

        // Start the global refresh before resolving the channel so a slow lookup cannot consume
        // the budget a cold global catalog needs (the same ordering as EmoteCatalogService).
        Task<BadgeScope?> globalTask = RefreshScopeAsync(
            _global, "global", null, linked.Token, ct, source.GetGlobalAsync);

        string? channelId;
        try
        {
            channelId = await ResolveChannelAsync(login, linked.Token, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller left. Observe the refresh already in flight, then propagate.
            await Task.WhenAll(globalTask).ConfigureAwait(false);
            throw;
        }

        Task<BadgeScope?> channelTask = channelId is null
            ? Task.FromResult<BadgeScope?>(null)
            : RefreshChannelAsync(channelId, ct);

        await Task.WhenAll(globalTask, channelTask).ConfigureAwait(false);

        return Combine(
            await globalTask.ConfigureAwait(false),
            await channelTask.ConfigureAwait(false));
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
                "Badge channel ID resolution timed out for {Login}; serving global badges only: {Reason}",
                login,
                DescribeFailure(exception).Reason);
            return null;
        }
    }

    /// <summary>
    /// Refreshes one channel under its own three-second budget, so a slow channel lookup cannot
    /// shorten the time the catalog itself is allowed.
    /// </summary>
    private async Task<BadgeScope?> RefreshChannelAsync(
        string channelId, CancellationToken callerCt)
    {
        using var budget = new CancellationTokenSource();
        using ITimer timer = clock.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            budget,
            FetchTimeout,
            Timeout.InfiniteTimeSpan);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(callerCt, budget.Token);

        if (!_channels.TryGetValue(channelId, out SingleFlightSlot<BadgeScope>? slot))
        {
            // Trim one below the limit so the entry about to be added still leaves at most
            // ChannelCacheLimit broadcasters in the cache.
            BoundedEmoteCache.Trim(
                _channels,
                ChannelCacheLimit - 1,
                clock.GetUtcNow(),
                CacheTtl,
                static cached => cached.FetchedAt,
                // An in-flight refresh has no value yet; evicting its slot would let a second
                // request start a duplicate fetch and detach the completed one.
                static slot => slot.Value is not null);
            slot = _channels.GetOrAdd(
                channelId, static _ => new SingleFlightSlot<BadgeScope>());
        }

        return await RefreshScopeAsync(
            slot,
            "channel",
            channelId,
            linked.Token,
            callerCt,
            token => source.GetChannelAsync(channelId, token));
    }

    /// <summary>
    /// Returns the scope's fresh cache, refreshes it under its single-flight gate, or falls back
    /// to its stale cache. A failure with no cache leaves the scope out.
    /// <see cref="BadgeScope.Stale"/> is decided here, when the scope is read, so a later scope's
    /// fetch cannot retroactively make an earlier fresh catalog stale.
    /// </summary>
    private async Task<BadgeScope?> RefreshScopeAsync(
        SingleFlightSlot<BadgeScope> slot,
        string scope,
        string? channelId,
        CancellationToken work,
        CancellationToken callerCt,
        Func<CancellationToken, Task<IReadOnlyList<BadgeCatalogEntry>>> fetch)
    {
        // The budget may have expired while this caller waited, or during channel-ID resolution
        // for the global scope.
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
                // Another refresh is still running and this caller's budget ran out.
                return Served(slot.Value, clock.GetUtcNow());
            }

            // A refresh that finished while this caller waited is reused.
            now = clock.GetUtcNow();
            if (IsFresh(slot.Value, now))
                return slot.Value;

            try
            {
                IReadOnlyList<BadgeCatalogEntry> entries = await fetch(work);
                DateTimeOffset fetchedAt = clock.GetUtcNow();
                var cached = new BadgeScope(entries, fetchedAt);
                slot.Value = cached;
                return cached;
            }
            catch (Exception exception) when (!callerCt.IsCancellationRequested)
            {
                LogRefreshFailure(scope, channelId, exception);
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
    /// Rolls up the two scopes: available when either served a catalog (including an empty one),
    /// stale when any served scope was past its TTL, and fetchedAt is the oldest fetch time.
    /// </summary>
    private static BadgeCatalogResult Combine(BadgeScope? global, BadgeScope? channel)
    {
        BadgeScope[] served = [.. new[] { global, channel }.OfType<BadgeScope>()];
        if (served.Length == 0)
            return BadgeCatalogResult.Unavailable;

        return new BadgeCatalogResult(
            Available: true,
            Stale: served.Any(scope => scope.Stale),
            FetchedAt: served.Min(scope => scope.FetchedAt),
            Global: global?.Entries ?? NoEntries,
            Channel: channel?.Entries ?? NoEntries);
    }

    private static bool IsFresh(BadgeScope? cached, DateTimeOffset now) =>
        cached is not null && now - cached.FetchedAt < CacheTtl;

    /// <summary>Marks a cached catalog stale when it is older than the TTL at read time.</summary>
    private static BadgeScope? Served(BadgeScope? cached, DateTimeOffset now) =>
        cached is null ? null : cached with { Stale = !IsFresh(cached, now) };

    private void LogRefreshFailure(string scope, string? channelId, Exception exception)
    {
        (int? httpStatus, string reason) = DescribeFailure(exception);

        // The exception message is our own text; no token, request, or response body is logged.
        logger.LogWarning(
            exception,
            "Badge catalog refresh failed for {Scope} badges " +
            "(channel {ChannelId}, HTTP {HttpStatus}): {Reason}",
            scope,
            channelId,
            httpStatus,
            reason);
    }

    // The reason vocabulary matches EmoteFailure.Describe so both catalogs log the same way.
    // The switch stays local because the badge and emote exceptions are separate seams.
    private static (int? HttpStatus, string Reason) DescribeFailure(Exception exception) =>
        exception switch
        {
            BadgeCatalogException { Kind: BadgeCatalogFailureKind.InvalidPayload } =>
                (null, "invalid-payload"),
            BadgeCatalogException { Kind: BadgeCatalogFailureKind.TooLarge } =>
                (null, "too-large"),
            BadgeCatalogException catalogError => (catalogError.HttpStatus, "http"),
            OperationCanceledException => (null, "timeout"),
            HttpRequestException requestError => ((int?)requestError.StatusCode, "transport"),
            TwitchApiException => (null, "twitch-api"),
            _ => (null, "error"),
        };

    private sealed record BadgeScope(
        IReadOnlyList<BadgeCatalogEntry> Entries, DateTimeOffset FetchedAt, bool Stale = false);
}
