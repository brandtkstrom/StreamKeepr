using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Emotes;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Emotes;

public sealed class EmoteCatalogServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly ListLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;

    public EmoteCatalogServiceTests() =>
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(_logs));

    [Fact]
    public async Task Disabled_feature_returns_unavailable_without_calling_any_provider()
    {
        var provider = new StubEmoteProvider();
        EmoteCatalogService service = Create([provider], enabled: false);

        EmoteCatalogResult result = await service.GetCatalogAsync(
            new EmoteChannel("chan", null), CancellationToken.None);

        Assert.False(result.Available);
        Assert.False(result.Stale);
        Assert.Null(result.FetchedAt);
        Assert.Empty(result.Entries);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task A_fresh_global_catalog_is_reused_within_the_five_minute_ttl()
    {
        var provider = new StubEmoteProvider
        {
            Fetch = (_, _) => Task.FromResult(
                new EmoteProviderCatalog([], [EmoteTestData.Entry(code: "KEKW")])),
        };
        EmoteCatalogService service = Create([provider]);

        EmoteCatalogResult first = await service.GetCatalogAsync(
            new EmoteChannel("chan", null), CancellationToken.None);
        EmoteCatalogResult second = await service.GetCatalogAsync(
            new EmoteChannel("chan", null), CancellationToken.None);

        Assert.True(first.Available);
        Assert.False(first.Stale);
        Assert.Equal(Now, first.FetchedAt);
        Assert.Equal("KEKW", Assert.Single(first.Entries).Entry.Code);
        Assert.Equal("bttv", first.Entries[0].Provider);
        Assert.Equal(Now, second.FetchedAt);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task A_global_catalog_refreshes_after_five_minutes()
    {
        var call = 0;
        var provider = new StubEmoteProvider
        {
            Fetch = (_, _) =>
            {
                int next = Interlocked.Increment(ref call);
                return Task.FromResult(new EmoteProviderCatalog(
                    [], [EmoteTestData.Entry(code: $"CODE{next}")]));
            },
        };
        EmoteCatalogService service = Create([provider]);

        EmoteCatalogResult first = await service.GetCatalogAsync(
            new EmoteChannel("chan", null), CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(5));
        EmoteCatalogResult second = await service.GetCatalogAsync(
            new EmoteChannel("chan", null), CancellationToken.None);

        Assert.Equal("CODE1", Assert.Single(first.Entries).Entry.Code);
        Assert.Equal("CODE2", Assert.Single(second.Entries).Entry.Code);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(Now.AddMinutes(5), second.FetchedAt);
    }

    [Fact]
    public async Task A_failed_refresh_without_a_cache_returns_unavailable_and_logs_once()
    {
        var provider = new StubEmoteProvider
        {
            Fetch = (_, _) => Task.FromException<EmoteProviderCatalog>(
                new EmoteProviderException("bttv", 503, "BetterTTV unavailable.")),
        };
        EmoteCatalogService service = Create([provider]);

        EmoteCatalogResult result = await service.GetCatalogAsync(
            new EmoteChannel("chan", "42"), CancellationToken.None);

        Assert.False(result.Available);
        Assert.False(result.Stale);
        Assert.Null(result.FetchedAt);
        Assert.Empty(result.Entries);
        string warning = Assert.Single(_logs.Messages);
        Assert.Contains("provider=bttv", warning, StringComparison.Ordinal);
        Assert.Contains("channel=42", warning, StringComparison.Ordinal);
        Assert.Contains("status=503", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_refresh_serves_the_stale_catalog()
    {
        var fail = false;
        var provider = new StubEmoteProvider
        {
            Fetch = (_, _) => fail
                ? Task.FromException<EmoteProviderCatalog>(
                    new EmoteProviderException("bttv", 500, "BetterTTV unavailable."))
                : Task.FromResult(new EmoteProviderCatalog(
                    [], [EmoteTestData.Entry(code: "KEKW")])),
        };
        EmoteCatalogService service = Create([provider]);

        await service.GetCatalogAsync(new EmoteChannel("chan", "42"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        fail = true;

        EmoteCatalogResult result = await service.GetCatalogAsync(
            new EmoteChannel("chan", "42"), CancellationToken.None);

        Assert.True(result.Available);
        Assert.True(result.Stale);
        Assert.Equal(Now, result.FetchedAt);
        Assert.Equal("KEKW", Assert.Single(result.Entries).Entry.Code);
        Assert.Equal(2, provider.Calls);
        Assert.Single(_logs.Messages);
    }

    [Fact]
    public async Task Concurrent_refreshes_share_one_provider_call()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<EmoteProviderCatalog>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubEmoteProvider
        {
            Fetch = (_, _) =>
            {
                started.TrySetResult();
                return release.Task;
            },
        };
        EmoteCatalogService service = Create([provider]);
        var channel = new EmoteChannel("chan", null);

        Task<EmoteCatalogResult> first = service.GetCatalogAsync(channel, CancellationToken.None);
        await started.Task;
        Task<EmoteCatalogResult>[] others = Enumerable.Range(0, 4)
            .Select(_ => service.GetCatalogAsync(channel, CancellationToken.None))
            .ToArray();
        await Task.Delay(50);

        release.SetResult(new EmoteProviderCatalog([], [EmoteTestData.Entry()]));
        EmoteCatalogResult[] results = await Task.WhenAll([first, .. others]);

        Assert.All(results, result => Assert.Single(result.Entries));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Provider_work_is_bounded_to_three_seconds()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubEmoteProvider
        {
            Fetch = async (_, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("unreachable");
            },
        };
        EmoteCatalogService service = Create([provider]);

        Task<EmoteCatalogResult> pending = service.GetCatalogAsync(
            new EmoteChannel("chan", "42"), CancellationToken.None);
        await started.Task;
        _clock.Advance(TimeSpan.FromSeconds(3));
        EmoteCatalogResult result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.Available);
        Assert.Empty(result.Entries);
        Assert.Single(_logs.Messages);
    }

    [Fact]
    public async Task Providers_merge_in_registration_order_and_dedupe_by_exact_code()
    {
        var first = new StubEmoteProvider
        {
            Id = "first",
            Fetch = (_, _) => Task.FromResult(new EmoteProviderCatalog(
                [],
                [
                    EmoteTestData.Entry(id: "aaaaaaaaaaaaaaaaaaaaaaaa", code: "KEKW"),
                    EmoteTestData.Entry(id: "bbbbbbbbbbbbbbbbbbbbbbbb", code: "brazil"),
                ])),
        };
        var second = new StubEmoteProvider
        {
            Id = "second",
            Fetch = (_, _) => Task.FromResult(new EmoteProviderCatalog(
                [],
                [
                    EmoteTestData.Entry(id: "cccccccccccccccccccccccc", code: "KEKW"),
                    EmoteTestData.Entry(id: "dddddddddddddddddddddddd", code: "Brazil"),
                ])),
        };
        EmoteCatalogService service = Create([first, second]);

        EmoteCatalogResult result = await service.GetCatalogAsync(
            new EmoteChannel("chan", null), CancellationToken.None);

        Assert.Equal(["KEKW", "brazil", "Brazil"], result.Entries.Select(item => item.Entry.Code));
        Assert.Equal(["first", "first", "second"], result.Entries.Select(item => item.Provider));
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaa", result.Entries[0].Entry.Id);
    }

    [Fact]
    public async Task Each_provider_global_catalog_is_capped_at_two_thousand_entries()
    {
        EmoteCatalogEntry[] entries = Enumerable.Range(0, 2_001)
            .Select(index => EmoteTestData.Entry(
                id: "aaaaaaaaaaaaaaaaaaaaaaaa", code: $"CODE{index}"))
            .ToArray();
        var provider = new StubEmoteProvider
        {
            Fetch = (_, _) => Task.FromResult(new EmoteProviderCatalog([], entries)),
        };
        EmoteCatalogService service = Create([provider]);

        EmoteCatalogResult result = await service.GetCatalogAsync(
            new EmoteChannel("chan", null), CancellationToken.None);

        Assert.Equal(2_000, result.Entries.Count);
        Assert.Equal("CODE0", result.Entries[0].Entry.Code);
        Assert.Equal("CODE1999", result.Entries[^1].Entry.Code);
    }

    [Fact]
    public async Task Modifier_and_invalid_provider_entries_are_dropped()
    {
        var provider = new StubEmoteProvider
        {
            Fetch = (_, _) => Task.FromResult(new EmoteProviderCatalog(
                [],
                [
                    EmoteTestData.Entry(id: "not-an-id", code: "BROKEN"),
                    EmoteTestData.Entry(code: ""),
                    EmoteTestData.Entry(code: "w!", modifier: true),
                    EmoteTestData.Entry(code: "KEKW"),
                ])),
        };
        EmoteCatalogService service = Create([provider]);

        EmoteCatalogResult result = await service.GetCatalogAsync(
            new EmoteChannel("chan", null), CancellationToken.None);

        Assert.Equal("KEKW", Assert.Single(result.Entries).Entry.Code);
    }

    [Fact]
    public async Task An_empty_provider_catalog_is_still_available()
    {
        var provider = new StubEmoteProvider
        {
            Fetch = (_, _) => Task.FromResult(new EmoteProviderCatalog([], [])),
        };
        EmoteCatalogService service = Create([provider]);

        EmoteCatalogResult result = await service.GetCatalogAsync(
            new EmoteChannel("chan", null), CancellationToken.None);

        Assert.True(result.Available);
        Assert.False(result.Stale);
        Assert.Equal(Now, result.FetchedAt);
        Assert.Empty(result.Entries);
    }

    private EmoteCatalogService Create(
        IEnumerable<IEmoteProvider> providers, bool enabled = true) =>
        new(providers,
            Options.Create(new ChatOptions
            {
                Emotes = new ChatEmoteOptions
                {
                    Bttv = new BttvEmoteOptions { Enabled = enabled },
                },
            }),
            _clock,
            _loggerFactory.CreateLogger<EmoteCatalogService>());
}
