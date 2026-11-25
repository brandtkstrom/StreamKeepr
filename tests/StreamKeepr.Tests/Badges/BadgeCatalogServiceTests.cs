using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Infrastructure.Badges;
using StreamKeepr.Tests.Emotes;
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
                [BadgeTestData.Entry()]),
        };
        BadgeCatalogService service = Create(source);

        BadgeCatalogResult first = await service.GetCatalogAsync(string.Empty, CancellationToken.None);
        BadgeCatalogResult second = await service.GetCatalogAsync(string.Empty, CancellationToken.None);

        Assert.True(first.Available);
        Assert.False(first.Stale);
        Assert.Equal(Now, first.FetchedAt);
        Assert.Equal("moderator", Assert.Single(first.Global).Set);
        Assert.Equal(first, second);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task A_resolved_channel_returns_its_catalog_beside_the_global_one()
    {
        string? requestedBroadcaster = null;
        var source = new StubBadgeSource
        {
            Global = _ => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                [BadgeTestData.Entry()]),
            Channel = (broadcasterId, _) =>
            {
                requestedBroadcaster = broadcasterId;
                return Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                [
                    BadgeTestData.Entry("subscriber", "0", "Nova Subscriber"),
                    BadgeTestData.Entry("subscriber", "3", "Nova Subscriber"),
                    BadgeTestData.Entry("bits", "100", "100 Bits"),
                ]);
            },
        };
        var resolver = new FakeEmoteChannelResolver("9100001");
        BadgeCatalogService service = Create(source, resolver);

        BadgeCatalogResult result =
            await service.GetCatalogAsync("mock_nova", CancellationToken.None);

        Assert.True(result.Available);
        Assert.False(result.Stale);
        Assert.Equal("moderator", Assert.Single(result.Global).Set);
        Assert.Equal(
            ["subscriber/0", "subscriber/3", "bits/100"],
            result.Channel.Select(entry => $"{entry.Set}/{entry.Version}"));
        Assert.Equal("9100001", requestedBroadcaster);
        Assert.Equal(1, resolver.Lookups);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, source.ChannelCalls);
    }

    [Fact]
    public async Task An_unresolved_channel_serves_global_badges_only()
    {
        var source = new StubBadgeSource
        {
            Global = _ => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                [BadgeTestData.Entry()]),
        };
        var resolver = new FakeEmoteChannelResolver();
        BadgeCatalogService service = Create(source, resolver);

        BadgeCatalogResult result =
            await service.GetCatalogAsync("unknown", CancellationToken.None);

        Assert.True(result.Available);
        Assert.False(result.Stale);
        Assert.Single(result.Global);
        Assert.Empty(result.Channel);
        Assert.Equal(0, source.ChannelCalls);
        Assert.Equal(1, resolver.Lookups);
    }

    [Fact]
    public async Task A_successful_empty_channel_catalog_is_available()
    {
        var source = new StubBadgeSource
        {
            Global = _ => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                [BadgeTestData.Entry()]),
            Channel = (_, _) => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>([]),
        };
        var resolver = new FakeEmoteChannelResolver("9100001");
        BadgeCatalogService service = Create(source, resolver);

        BadgeCatalogResult result =
            await service.GetCatalogAsync("mock_nova", CancellationToken.None);

        Assert.True(result.Available);
        Assert.False(result.Stale);
        Assert.Single(result.Global);
        Assert.Empty(result.Channel);
    }

    [Fact]
    public async Task The_catalog_refreshes_once_the_hour_has_elapsed()
    {
        BadgeCatalogEntry firstEntry = BadgeTestData.Entry(title: "First");
        BadgeCatalogEntry secondEntry = BadgeTestData.Entry(title: "Second");
        int call = 0;
        var source = new StubBadgeSource
        {
            Global = _ => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                [call++ == 0 ? firstEntry : secondEntry]),
        };
        BadgeCatalogService service = Create(source);

        await service.GetCatalogAsync(string.Empty, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
        BadgeCatalogResult stillFresh = await service.GetCatalogAsync(string.Empty, CancellationToken.None);
        Assert.Equal("First", Assert.Single(stillFresh.Global).Title);
        Assert.Equal(1, source.Calls);

        _clock.Advance(TimeSpan.FromSeconds(1));
        BadgeCatalogResult refreshed = await service.GetCatalogAsync(string.Empty, CancellationToken.None);

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

        BadgeCatalogResult result = await service.GetCatalogAsync(string.Empty, CancellationToken.None);

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
                [BadgeTestData.Entry()]),
        };
        BadgeCatalogService service = Create(source);
        BadgeCatalogResult first = await service.GetCatalogAsync(string.Empty, CancellationToken.None);

        _clock.Advance(BadgeCatalogService.CacheTtl);
        source.Global = _ => throw BadgeCatalogException.HttpFailure(503);
        BadgeCatalogResult stale = await service.GetCatalogAsync(string.Empty, CancellationToken.None);

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

        BadgeCatalogResult result = await service.GetCatalogAsync(string.Empty, CancellationToken.None);

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

        Task<BadgeCatalogResult> first = service.GetCatalogAsync(string.Empty, CancellationToken.None);
        await started.Task;
        Task<BadgeCatalogResult> second = service.GetCatalogAsync(string.Empty, CancellationToken.None);
        release.SetResult([BadgeTestData.Entry()]);

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

        Task<BadgeCatalogResult> pending = service.GetCatalogAsync(string.Empty, CancellationToken.None);
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

                return [BadgeTestData.Entry()];
            },
        };
        BadgeCatalogService service = Create(source);
        await service.GetCatalogAsync(string.Empty, CancellationToken.None);

        _clock.Advance(BadgeCatalogService.CacheTtl);
        serve = false;
        Task<BadgeCatalogResult> pending = service.GetCatalogAsync(string.Empty, CancellationToken.None);
        _clock.Advance(BadgeCatalogService.FetchTimeout);
        BadgeCatalogResult result = await pending;

        Assert.True(result.Available);
        Assert.True(result.Stale);
        Assert.Single(result.Global);
    }

    [Fact]
    public async Task The_channel_catalog_is_cached_per_broadcaster()
    {
        var source = new StubBadgeSource
        {
            Global = _ => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                [BadgeTestData.Entry()]),
            Channel = (broadcasterId, _) =>
                Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                    [BadgeTestData.Entry("bits", broadcasterId)]),
        };
        var resolver = new FakeEmoteChannelResolver
        {
            Resolve = (login, _) => Task.FromResult<string?>(
                login == "one" ? "111" : "222"),
        };
        BadgeCatalogService service = Create(source, resolver);

        BadgeCatalogResult first =
            await service.GetCatalogAsync("one", CancellationToken.None);
        BadgeCatalogResult again =
            await service.GetCatalogAsync("one", CancellationToken.None);
        BadgeCatalogResult other =
            await service.GetCatalogAsync("two", CancellationToken.None);

        Assert.Equal("111", Assert.Single(first.Channel).Version);
        Assert.Equal("111", Assert.Single(again.Channel).Version);
        Assert.Equal("222", Assert.Single(other.Channel).Version);
        Assert.Equal(1, source.Calls);
        Assert.Equal(2, source.ChannelCalls);
    }

    [Fact]
    public async Task A_channel_failure_without_a_cache_still_serves_the_global_catalog()
    {
        var source = new StubBadgeSource
        {
            Global = _ => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                [BadgeTestData.Entry()]),
            Channel = (_, _) => throw BadgeCatalogException.HttpFailure(503),
        };
        BadgeCatalogService service =
            Create(source, new FakeEmoteChannelResolver("9100001"));

        BadgeCatalogResult result =
            await service.GetCatalogAsync("mock_nova", CancellationToken.None);

        Assert.True(result.Available);
        Assert.False(result.Stale);
        Assert.Equal(Now, result.FetchedAt);
        Assert.Single(result.Global);
        Assert.Empty(result.Channel);
        Assert.Contains(
            _logs.Entries,
            entry => entry.Level == LogLevel.Warning &&
                entry.Message.Contains("channel", StringComparison.Ordinal) &&
                entry.Message.Contains("HTTP 503", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_global_failure_without_a_cache_still_serves_the_channel_catalog()
    {
        var source = new StubBadgeSource
        {
            Global = _ => throw BadgeCatalogException.HttpFailure(503),
            Channel = (_, _) => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                [BadgeTestData.Entry("subscriber", "0", "Nova Subscriber")]),
        };
        BadgeCatalogService service =
            Create(source, new FakeEmoteChannelResolver("9100001"));

        BadgeCatalogResult result =
            await service.GetCatalogAsync("mock_nova", CancellationToken.None);

        Assert.True(result.Available);
        Assert.False(result.Stale);
        Assert.Equal(Now, result.FetchedAt);
        Assert.Empty(result.Global);
        Assert.Equal("Nova Subscriber", Assert.Single(result.Channel).Title);
    }

    [Fact]
    public async Task A_channel_failure_serves_the_stale_channel_and_refreshes_global()
    {
        long globalFetches = 0;
        long channelFetches = 0;
        var source = new StubBadgeSource
        {
            Global = _ =>
            {
                globalFetches++;
                return Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                    [BadgeTestData.Entry(title: $"Global {globalFetches}")]);
            },
            Channel = (_, _) =>
            {
                channelFetches++;
                return channelFetches == 1
                    ? Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                        [BadgeTestData.Entry("subscriber", "0", "Cached Channel")])
                    : throw BadgeCatalogException.HttpFailure(503);
            },
        };
        BadgeCatalogService service =
            Create(source, new FakeEmoteChannelResolver("9100001"));
        await service.GetCatalogAsync("mock_nova", CancellationToken.None);

        _clock.Advance(BadgeCatalogService.CacheTtl);
        BadgeCatalogResult result =
            await service.GetCatalogAsync("mock_nova", CancellationToken.None);

        Assert.True(result.Available);
        Assert.True(result.Stale);
        Assert.Equal(Now, result.FetchedAt);
        Assert.Equal("Global 2", Assert.Single(result.Global).Title);
        Assert.Equal("Cached Channel", Assert.Single(result.Channel).Title);
        Assert.Equal(2, source.Calls);
        Assert.Equal(2, source.ChannelCalls);
    }

    [Fact]
    public async Task A_global_failure_keeps_the_cached_channel_catalog()
    {
        long globalFetches = 0;
        long channelFetches = 0;
        var source = new StubBadgeSource
        {
            Global = _ =>
            {
                globalFetches++;
                return globalFetches == 1
                    ? Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                        [BadgeTestData.Entry(title: "Cached Global")])
                    : throw BadgeCatalogException.HttpFailure(503);
            },
            Channel = (_, _) =>
            {
                channelFetches++;
                return Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                    [BadgeTestData.Entry("bits", "100", $"Channel {channelFetches}")]);
            },
        };
        BadgeCatalogService service =
            Create(source, new FakeEmoteChannelResolver("9100001"));
        await service.GetCatalogAsync("mock_nova", CancellationToken.None);

        _clock.Advance(BadgeCatalogService.CacheTtl);
        BadgeCatalogResult result =
            await service.GetCatalogAsync("mock_nova", CancellationToken.None);

        Assert.True(result.Available);
        Assert.True(result.Stale);
        Assert.Equal(Now, result.FetchedAt);
        Assert.Equal("Cached Global", Assert.Single(result.Global).Title);
        Assert.Equal("Channel 2", Assert.Single(result.Channel).Title);
    }

    [Fact]
    public async Task Concurrent_channel_refreshes_share_one_source_call()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<BadgeCatalogEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new StubBadgeSource
        {
            Global = _ => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                [BadgeTestData.Entry()]),
            Channel = (_, _) =>
            {
                started.TrySetResult();
                return release.Task;
            },
        };
        BadgeCatalogService service =
            Create(source, new FakeEmoteChannelResolver("9100001"));

        Task<BadgeCatalogResult> first =
            service.GetCatalogAsync("mock_nova", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<BadgeCatalogResult> second =
            service.GetCatalogAsync("mock_nova", CancellationToken.None);
        release.SetResult([BadgeTestData.Entry("subscriber", "0")]);

        BadgeCatalogResult[] results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Single(result.Channel));
        Assert.Equal(1, source.ChannelCalls);
    }

    [Fact]
    public async Task A_channel_resolution_timeout_serves_global_badges()
    {
        var source = new StubBadgeSource
        {
            Global = _ => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                [BadgeTestData.Entry()]),
        };
        var resolver = new FakeEmoteChannelResolver { Hang = true };
        BadgeCatalogService service = Create(source, resolver);

        Task<BadgeCatalogResult> pending =
            service.GetCatalogAsync("mock_nova", CancellationToken.None);
        await resolver.Started.WaitAsync(TimeSpan.FromSeconds(10));
        _clock.Advance(BadgeCatalogService.FetchTimeout);
        BadgeCatalogResult result = await pending;

        Assert.True(result.Available);
        Assert.Single(result.Global);
        Assert.Empty(result.Channel);
        Assert.Equal(0, source.ChannelCalls);
        Assert.Contains(
            _logs.Entries,
            entry => entry.Message.Contains("timeout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_slow_channel_resolution_does_not_starve_a_cold_channel_catalog()
    {
        var source = new StubBadgeSource
        {
            Global = _ => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                [BadgeTestData.Entry()]),
            Channel = async (_, ct) =>
            {
                // The global/resolution budget expires at 3 s; the channel has its own budget.
                _clock.Advance(TimeSpan.FromMilliseconds(200));
                ct.ThrowIfCancellationRequested();
                return [BadgeTestData.Entry("subscriber", "0", "Nova Subscriber")];
            },
        };
        var resolver = new FakeEmoteChannelResolver
        {
            Resolve = (_, _) =>
            {
                _clock.Advance(TimeSpan.FromSeconds(2.9));
                return Task.FromResult<string?>("9100001");
            },
        };
        BadgeCatalogService service = Create(source, resolver);

        BadgeCatalogResult result =
            await service.GetCatalogAsync("mock_nova", CancellationToken.None);

        Assert.True(result.Available);
        Assert.False(result.Stale);
        Assert.Single(result.Global);
        Assert.Equal("Nova Subscriber", Assert.Single(result.Channel).Title);
    }

    [Fact]
    public async Task A_channel_timeout_serves_the_stale_channel_and_refreshes_global()
    {
        var hang = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new StubBadgeSource
        {
            Global = _ => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                [BadgeTestData.Entry()]),
            Channel = async (_, ct) =>
            {
                if (!hang)
                    return [BadgeTestData.Entry("subscriber", "0", "Cached Channel")];

                started.TrySetResult();
                await EmoteTestData.NeverAsync(ct);
                return [];
            },
        };
        BadgeCatalogService service =
            Create(source, new FakeEmoteChannelResolver("9100001"));
        await service.GetCatalogAsync("mock_nova", CancellationToken.None);

        _clock.Advance(BadgeCatalogService.CacheTtl);
        hang = true;
        Task<BadgeCatalogResult> pending =
            service.GetCatalogAsync("mock_nova", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _clock.Advance(BadgeCatalogService.FetchTimeout);
        BadgeCatalogResult result = await pending;

        Assert.True(result.Available);
        Assert.True(result.Stale);
        Assert.Single(result.Global);
        Assert.Equal("Cached Channel", Assert.Single(result.Channel).Title);
    }

    [Fact]
    public async Task The_channel_cache_holds_256_broadcasters_and_evicts_beyond_that()
    {
        var source = new StubBadgeSource
        {
            Global = _ => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
                [BadgeTestData.Entry()]),
        };
        var resolver = new FakeEmoteChannelResolver
        {
            Resolve = (login, _) => Task.FromResult<string?>(login["chan".Length..]),
        };
        BadgeCatalogService service = Create(source, resolver);

        // All 256 fit: re-reading every broadcaster makes no new channel request.
        for (int index = 0; index < 256; index++)
            await service.GetCatalogAsync($"chan{index}", CancellationToken.None);
        for (int index = 0; index < 256; index++)
            await service.GetCatalogAsync($"chan{index}", CancellationToken.None);
        Assert.Equal(256, source.ChannelCalls);

        // The 257th is fetched but must evict an older entry, so re-reading all of them
        // refetches at least one catalog.
        await service.GetCatalogAsync("chan256", CancellationToken.None);
        for (int index = 0; index < 257; index++)
            await service.GetCatalogAsync($"chan{index}", CancellationToken.None);
        Assert.True(source.ChannelCalls > 257);
        Assert.Equal(1, source.Calls);
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

        Task<BadgeCatalogResult> pending = service.GetCatalogAsync(string.Empty, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private BadgeCatalogService Create(
        StubBadgeSource source, FakeEmoteChannelResolver? resolver = null) =>
        new(
            source,
            resolver ?? new FakeEmoteChannelResolver(),
            _clock,
            _loggerFactory.CreateLogger<BadgeCatalogService>());
}
