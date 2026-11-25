using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Infrastructure.Badges;

/// <summary>The global badge catalog and the freshness of the copy that served it.</summary>
internal sealed record BadgeCatalogResult(
    bool Available,
    bool Stale,
    DateTimeOffset? FetchedAt,
    IReadOnlyList<BadgeCatalogEntry> Global)
{
    public static BadgeCatalogResult Unavailable { get; } = new(false, false, null, []);
}

/// <summary>
/// Caches Twitch's global badge catalog for an hour. One in-flight refresh serves every caller,
/// a failed refresh serves the last good catalog, and a failure with no cache leaves the catalog
/// unavailable. Badge failures never surface as errors and never affect chat, emotes, capture,
/// recording, or health.
/// </summary>
internal sealed class BadgeCatalogService(
    IBadgeSource source,
    TimeProvider clock,
    ILogger<BadgeCatalogService> logger)
{
    /// <summary>Badges change rarely; an hour keeps the app off Helix without visible drift.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(60);

    /// <summary>The whole refresh shares this budget, including token acquisition.</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile BadgeCatalogSnapshot? _cached;

    public async Task<BadgeCatalogResult> GetGlobalAsync(CancellationToken ct)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (IsFresh(_cached, now))
            return Served(_cached, now);

        using var budget = new CancellationTokenSource();
        using ITimer timer = clock.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            budget,
            FetchTimeout,
            Timeout.InfiniteTimeSpan);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);

        bool acquired = false;
        try
        {
            try
            {
                await _gate.WaitAsync(linked.Token);
                acquired = true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Another refresh is still running and this caller's budget ran out.
                return Served(_cached, clock.GetUtcNow());
            }

            // A refresh that finished while this caller waited is reused.
            now = clock.GetUtcNow();
            if (IsFresh(_cached, now))
                return Served(_cached, now);

            try
            {
                IReadOnlyList<BadgeCatalogEntry> entries = await source.GetGlobalAsync(linked.Token);
                DateTimeOffset fetchedAt = clock.GetUtcNow();
                var cached = new BadgeCatalogSnapshot(entries, fetchedAt);
                _cached = cached;
                return Served(cached, fetchedAt);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                LogRefreshFailure(exception);
                return Served(_cached, clock.GetUtcNow());
            }
        }
        finally
        {
            if (acquired)
                _gate.Release();
        }
    }

    private static bool IsFresh(BadgeCatalogSnapshot? cached, DateTimeOffset now) =>
        cached is not null && now - cached.FetchedAt < CacheTtl;

    /// <summary>Marks a cached catalog stale when it is older than the TTL at read time.</summary>
    private static BadgeCatalogResult Served(BadgeCatalogSnapshot? cached, DateTimeOffset now) =>
        cached is null
            ? BadgeCatalogResult.Unavailable
            : new(
                true,
                now - cached.FetchedAt >= CacheTtl,
                cached.FetchedAt,
                cached.Entries);

    private void LogRefreshFailure(Exception exception)
    {
        (int? httpStatus, string reason) = DescribeFailure(exception);

        // The exception message is our own text; no token, request, or response body is logged.
        logger.LogWarning(
            exception,
            "Global badge catalog refresh failed (HTTP {HttpStatus}): {Reason}",
            httpStatus,
            reason);
    }

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

    private sealed record BadgeCatalogSnapshot(
        IReadOnlyList<BadgeCatalogEntry> Entries, DateTimeOffset FetchedAt);
}
