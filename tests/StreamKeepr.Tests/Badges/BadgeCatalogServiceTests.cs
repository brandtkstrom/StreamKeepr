using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Infrastructure.Badges;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Badges;

public sealed class BadgeCatalogServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly ListLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;

    public BadgeCatalogServiceTests() =>
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(_logs));

    [Fact]
    public async Task A_fresh_catalog_is_reused_within_the_sixty_minute_ttl()
    {
        var source = new StubBadgeSource
        {
            Global = _ => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                [StubBadgeSource.Entry()]),
        };
        BadgeCatalogService service = Create(source);

        BadgeCatalogResult first = await service.GetGlobalAsync(CancellationToken.None);
        BadgeCatalogResult second = await service.GetGlobalAsync(CancellationToken.None);

        Assert.True(first.Available);
        Assert.False(first.Stale);
        Assert.Equal(Now, first.FetchedAt);
        Assert.Equal("moderator", Assert.Single(first.Global).Set);
        Assert.Equal(first, second);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task The_catalog_refreshes_once_the_hour_has_elapsed()
    {
        BadgeCatalogEntry firstEntry = StubBadgeSource.Entry(title: "First");
        BadgeCatalogEntry secondEntry = StubBadgeSource.Entry(title: "Second");
        int call = 0;
        var source = new StubBadgeSource
        {
            Global = _ => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                [call++ == 0 ? firstEntry : secondEntry]),
        };
        BadgeCatalogService service = Create(source);

        await service.GetGlobalAsync(CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
        BadgeCatalogResult stillFresh = await service.GetGlobalAsync(CancellationToken.None);
        Assert.Equal("First", Assert.Single(stillFresh.Global).Title);
        Assert.Equal(1, source.Calls);

        _clock.Advance(TimeSpan.FromSeconds(1));
        BadgeCatalogResult refreshed = await service.GetGlobalAsync(CancellationToken.None);

        Assert.False(refreshed.Stale);
        Assert.Equal("Second", Assert.Single(refreshed.Global).Title);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task A_successful_empty_catalog_is_available()
    {
        var source = new StubBadgeSource
        {
            Global = _ => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>([]),
        };
        BadgeCatalogService service = Create(source);

        BadgeCatalogResult result = await service.GetGlobalAsync(CancellationToken.None);

        Assert.True(result.Available);
        Assert.False(result.Stale);
        Assert.Equal(Now, result.FetchedAt);
        Assert.Empty(result.Global);
    }

    [Fact]
    public async Task A_failed_refresh_serves_the_stale_catalog()
    {
        var source = new StubBadgeSource
        {
            Global = _ => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                [StubBadgeSource.Entry()]),
        };
        BadgeCatalogService service = Create(source);
        BadgeCatalogResult first = await service.GetGlobalAsync(CancellationToken.None);

        _clock.Advance(BadgeCatalogService.CacheTtl);
        source.Global = _ => throw BadgeCatalogException.HttpFailure(503);
        BadgeCatalogResult stale = await service.GetGlobalAsync(CancellationToken.None);

        Assert.True(stale.Available);
        Assert.True(stale.Stale);
        Assert.Equal(first.FetchedAt, stale.FetchedAt);
        Assert.Equal("moderator", Assert.Single(stale.Global).Set);
        Assert.Equal(2, source.Calls);
        Assert.Contains(
            _logs.Entries,
            entry => entry.Level == LogLevel.Warning &&
                entry.Message.Contains("HTTP 503", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failure_with_no_cache_is_unavailable_and_never_throws()
    {
        var source = new StubBadgeSource
        {
            Global = _ => throw BadgeCatalogException.InvalidPayload(),
        };
        BadgeCatalogService service = Create(source);

        BadgeCatalogResult result = await service.GetGlobalAsync(CancellationToken.None);

        Assert.False(result.Available);
        Assert.False(result.Stale);
        Assert.Null(result.FetchedAt);
        Assert.Empty(result.Global);
        Assert.Contains(
            _logs.Entries,
            entry => entry.Message.Contains("invalid-payload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Concurrent_refreshes_share_one_source_call()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<BadgeCatalogEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new StubBadgeSource
        {
            Global = _ =>
            {
                started.TrySetResult();
                return release.Task;
            },
        };
        BadgeCatalogService service = Create(source);

        Task<BadgeCatalogResult> first = service.GetGlobalAsync(CancellationToken.None);
        await started.Task;
        Task<BadgeCatalogResult> second = service.GetGlobalAsync(CancellationToken.None);
        release.SetResult([StubBadgeSource.Entry()]);

        BadgeCatalogResult[] results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Single(result.Global));
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task A_timeout_with_no_cache_is_unavailable()
    {
        var source = new StubBadgeSource
        {
            Global = async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return [];
            },
        };
        BadgeCatalogService service = Create(source);

        Task<BadgeCatalogResult> pending = service.GetGlobalAsync(CancellationToken.None);
        _clock.Advance(BadgeCatalogService.FetchTimeout);
        BadgeCatalogResult result = await pending;

        Assert.False(result.Available);
        Assert.Equal(1, source.Calls);
        Assert.Contains(
            _logs.Entries,
            entry => entry.Message.Contains("timeout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_timeout_after_a_successful_fetch_serves_the_stale_catalog()
    {
        var serve = true;
        var source = new StubBadgeSource
        {
            Global = async ct =>
            {
                if (!serve)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }

                return [StubBadgeSource.Entry()];
            },
        };
        BadgeCatalogService service = Create(source);
        await service.GetGlobalAsync(CancellationToken.None);

        _clock.Advance(BadgeCatalogService.CacheTtl);
        serve = false;
        Task<BadgeCatalogResult> pending = service.GetGlobalAsync(CancellationToken.None);
        _clock.Advance(BadgeCatalogService.FetchTimeout);
        BadgeCatalogResult result = await pending;

        Assert.True(result.Available);
        Assert.True(result.Stale);
        Assert.Single(result.Global);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        var source = new StubBadgeSource
        {
            Global = async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return [];
            },
        };
        BadgeCatalogService service = Create(source);
        using var cts = new CancellationTokenSource();

        Task<BadgeCatalogResult> pending = service.GetGlobalAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private BadgeCatalogService Create(StubBadgeSource source) =>
        new(source, _clock, _loggerFactory.CreateLogger<BadgeCatalogService>());
}
