using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
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
    public async Task A_disabled_provider_returns_unavailable_without_calling_any_provider()
    {
        var provider = new StubEmoteProvider
        {
            Enabled = false,
            Global = _ => Succeed(EmoteTestData.Entry()),
        };
        var resolver = new FakeEmoteChannelResolver("42");
        EmoteCatalogService service = Create([provider], resolver);

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.False(result.Available);
        Assert.False(result.Stale);
        Assert.Null(result.FetchedAt);
        Assert.Empty(result.Entries);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, resolver.Lookups);
    }

    [Fact]
    public async Task A_disabled_provider_is_skipped_and_an_enabled_provider_is_served()
    {
        var disabled = new StubEmoteProvider
        {
            Id = "disabled",
            Enabled = false,
            Global = _ => Succeed(EmoteTestData.Entry(code: "DISABLED")),
        };
        var enabled = new StubEmoteProvider
        {
            Id = "enabled",
            Global = _ => Succeed(EmoteTestData.Entry(code: "KEKW")),
        };
        EmoteCatalogService service = Create([disabled, enabled]);

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.Equal("KEKW", Assert.Single(result.Entries).Entry.Code);
        Assert.Equal("enabled", result.Entries[0].Provider);
        Assert.Equal(0, disabled.Calls);
        Assert.Equal(1, enabled.GlobalCalls);
    }

    [Fact]
    public async Task A_fresh_global_catalog_is_reused_within_the_five_minute_ttl()
    {
        var provider = new StubEmoteProvider
        {
            Global = _ => Succeed(EmoteTestData.Entry(code: "KEKW")),
        };
        EmoteCatalogService service = Create([provider]);

        EmoteCatalogResult first = await service.GetCatalogAsync("chan", CancellationToken.None);
        EmoteCatalogResult second = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.True(first.Available);
        Assert.False(first.Stale);
        Assert.Equal(Now, first.FetchedAt);
        Assert.Equal("KEKW", Assert.Single(first.Entries).Entry.Code);
        Assert.Equal("bttv", first.Entries[0].Provider);
        Assert.Equal(Now, second.FetchedAt);
        Assert.Equal(1, provider.GlobalCalls);
        Assert.Equal(0, provider.ChannelCalls);
    }

    [Fact]
    public async Task A_global_catalog_refreshes_after_five_minutes()
    {
        var call = 0;
        var provider = new StubEmoteProvider
        {
            Global = _ =>
            {
                int next = Interlocked.Increment(ref call);
                return Succeed(EmoteTestData.Entry(code: $"CODE{next}"));
            },
        };
        EmoteCatalogService service = Create([provider]);

        EmoteCatalogResult first = await service.GetCatalogAsync("chan", CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(5));
        EmoteCatalogResult second = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.Equal("CODE1", Assert.Single(first.Entries).Entry.Code);
        Assert.Equal("CODE2", Assert.Single(second.Entries).Entry.Code);
        Assert.Equal(2, provider.GlobalCalls);
        Assert.Equal(Now.AddMinutes(5), second.FetchedAt);
    }

    [Fact]
    public async Task A_failed_global_refresh_without_a_cache_returns_unavailable_and_logs_once()
    {
        var provider = new StubEmoteProvider
        {
            Global = _ => Fail(new EmoteProviderException("bttv", 503, "BetterTTV unavailable.")),
        };
        EmoteCatalogService service = Create([provider]);

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.False(result.Available);
        Assert.False(result.Stale);
        Assert.Null(result.FetchedAt);
        Assert.Empty(result.Entries);
        CapturedLogEntry warning = Assert.Single(_logs.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        AssertRefreshFailure(warning, scope: "global", channelId: null, httpStatus: 503, reason: "http");
    }

    [Fact]
    public async Task A_failed_channel_refresh_without_a_cache_keeps_the_global_catalog()
    {
        var provider = new StubEmoteProvider
        {
            Global = _ => Succeed(EmoteTestData.Entry(code: "KEKW")),
            Channel = (_, _) => Fail(
                new EmoteProviderException("bttv", 500, "BetterTTV unavailable.")),
        };
        EmoteCatalogService service = Create([provider], new FakeEmoteChannelResolver("42"));

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.True(result.Available);
        Assert.False(result.Stale);
        Assert.Equal(Now, result.FetchedAt);
        Assert.Equal("KEKW", Assert.Single(result.Entries).Entry.Code);
        CapturedLogEntry warning = Assert.Single(_logs.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        AssertRefreshFailure(warning, scope: "channel", channelId: "42", httpStatus: 500, reason: "http");
    }

    [Fact]
    public async Task A_failed_global_refresh_without_a_cache_keeps_the_channel_catalog()
    {
        var provider = new StubEmoteProvider
        {
            Global = _ => Fail(
                new EmoteProviderException("bttv", 500, "BetterTTV unavailable.")),
            Channel = (_, _) => Succeed(EmoteTestData.Entry(code: "CHANNEL")),
        };
        EmoteCatalogService service = Create([provider], new FakeEmoteChannelResolver("42"));

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.True(result.Available);
        Assert.False(result.Stale);
        Assert.Equal(Now, result.FetchedAt);
        Assert.Equal("CHANNEL", Assert.Single(result.Entries).Entry.Code);
        AssertRefreshFailure(
            Assert.Single(_logs.Entries), scope: "global", channelId: null, httpStatus: 500, reason: "http");
    }

    [Fact]
    public async Task A_failed_global_refresh_serves_the_stale_catalog()
    {
        var fail = false;
        var provider = new StubEmoteProvider
        {
            Global = _ => fail
                ? Fail(new EmoteProviderException("bttv", 500, "BetterTTV unavailable."))
                : Succeed(EmoteTestData.Entry(code: "KEKW")),
        };
        EmoteCatalogService service = Create([provider]);

        await service.GetCatalogAsync("chan", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        fail = true;

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.True(result.Available);
        Assert.True(result.Stale);
        Assert.Equal(Now, result.FetchedAt);
        Assert.Equal("KEKW", Assert.Single(result.Entries).Entry.Code);
        Assert.Equal(2, provider.GlobalCalls);
        Assert.Single(_logs.Messages);
    }

    [Fact]
    public async Task Concurrent_global_refreshes_share_one_provider_call()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<EmoteCatalogEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubEmoteProvider
        {
            Global = _ =>
            {
                started.TrySetResult();
                return release.Task;
            },
        };
        EmoteCatalogService service = Create([provider]);

        Task<EmoteCatalogResult> first = service.GetCatalogAsync("chan", CancellationToken.None);
        await started.Task;
        Task<EmoteCatalogResult>[] others = Enumerable.Range(0, 4)
            .Select(_ => service.GetCatalogAsync("chan", CancellationToken.None))
            .ToArray();

        release.SetResult([EmoteTestData.Entry()]);
        EmoteCatalogResult[] results = await Task.WhenAll([first, .. others]);

        Assert.All(results, result => Assert.Single(result.Entries));
        Assert.Equal(1, provider.GlobalCalls);
    }

    [Fact]
    public async Task Concurrent_global_refreshes_share_one_call_across_channels()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<EmoteCatalogEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubEmoteProvider
        {
            Global = _ =>
            {
                started.TrySetResult();
                return release.Task;
            },
        };
        var resolver = new FakeEmoteChannelResolver
        {
            Resolve = (login, _) => Task.FromResult<string?>(login == "first" ? "42" : "43"),
        };
        EmoteCatalogService service = Create([provider], resolver);

        Task<EmoteCatalogResult> first = service.GetCatalogAsync("first", CancellationToken.None);
        await started.Task;
        Task<EmoteCatalogResult>[] others = Enumerable.Range(0, 4)
            .Select(_ => service.GetCatalogAsync("other", CancellationToken.None))
            .ToArray();

        release.SetResult([EmoteTestData.Entry(code: "GLOBAL")]);
        EmoteCatalogResult[] results = await Task.WhenAll([first, .. others]);

        Assert.All(results, result => Assert.Single(result.Entries));
        Assert.Equal(1, provider.GlobalCalls);
        Assert.Equal(2, provider.ChannelCalls);
    }

    [Fact]
    public async Task Provider_work_is_bounded_to_three_seconds()
    {
        var globalStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var channelStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubEmoteProvider
        {
            Global = async ct =>
            {
                globalStarted.TrySetResult();
                await EmoteTestData.NeverAsync(ct);
                throw new InvalidOperationException("unreachable");
            },
            Channel = async (_, ct) =>
            {
                channelStarted.TrySetResult();
                await EmoteTestData.NeverAsync(ct);
                throw new InvalidOperationException("unreachable");
            },
        };
        EmoteCatalogService service = Create([provider], new FakeEmoteChannelResolver("42"));

        Task<EmoteCatalogResult> pending = service.GetCatalogAsync(
            "chan", CancellationToken.None);
        await Task.WhenAll(globalStarted.Task, channelStarted.Task);
        _clock.Advance(TimeSpan.FromSeconds(3));
        EmoteCatalogResult result = await pending;

        Assert.False(result.Available);
        Assert.Empty(result.Entries);
        Assert.Equal(2, _logs.Entries.Count);
        Assert.All(_logs.Entries, warning =>
        {
            Assert.Equal("timeout", warning.Properties["Reason"]);
            Assert.Null(warning.Properties["HttpStatus"]);
        });
    }

    [Fact]
    public async Task Providers_merge_in_registration_order_and_dedupe_by_exact_code()
    {
        var first = new StubEmoteProvider
        {
            Id = "first",
            Global = _ => Succeed(
                EmoteTestData.Entry(id: "aaaaaaaaaaaaaaaaaaaaaaaa", code: "KEKW"),
                EmoteTestData.Entry(id: "bbbbbbbbbbbbbbbbbbbbbbbb", code: "brazil")),
        };
        var second = new StubEmoteProvider
        {
            Id = "second",
            Global = _ => Succeed(
                EmoteTestData.Entry(id: "cccccccccccccccccccccccc", code: "KEKW"),
                EmoteTestData.Entry(id: "dddddddddddddddddddddddd", code: "Brazil")),
        };
        EmoteCatalogService service = Create([first, second]);

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.Equal(["KEKW", "brazil", "Brazil"], result.Entries.Select(item => item.Entry.Code));
        Assert.Equal(["first", "first", "second"], result.Entries.Select(item => item.Provider));
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaa", result.Entries[0].Entry.Id);
    }

    [Fact]
    public async Task A_seven_tv_ulid_survives_the_shared_usability_check()
    {
        var provider = new StubEmoteProvider
        {
            Id = "7tv",
            Global = _ => Succeed(
                EmoteTestData.Entry(id: "01GGD5PJA8000FH13S498E9D8X", code: "ppL")),
        };
        EmoteCatalogService service = Create([provider]);

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        EmoteCatalogItem item = Assert.Single(result.Entries);
        Assert.Equal("ppL", item.Entry.Code);
        Assert.Equal("01GGD5PJA8000FH13S498E9D8X", item.Entry.Id);
    }

    [Fact]
    public async Task An_ffz_numeric_id_survives_the_shared_usability_check()
    {
        var provider = new StubEmoteProvider
        {
            Id = "ffz",
            Global = _ => Succeed(EmoteTestData.Entry(id: "381875", code: "KEKW")),
        };
        EmoteCatalogService service = Create([provider]);

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        EmoteCatalogItem item = Assert.Single(result.Entries);
        Assert.Equal("ffz", item.Provider);
        Assert.Equal("381875", item.Entry.Id);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("000")]
    [InlineData("12a")]
    [InlineData("123456789012345678901")]
    public async Task Invalid_ffz_numeric_ids_are_dropped_by_the_shared_usability_check(string id)
    {
        var provider = new StubEmoteProvider
        {
            Id = "ffz",
            Global = _ => Succeed(EmoteTestData.Entry(id: id, code: "BROKEN")),
        };
        EmoteCatalogService service = Create([provider]);

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task A_duplicate_code_across_betterttv_and_seven_tv_keeps_the_first_provider()
    {
        var bttv = new StubEmoteProvider
        {
            Id = "bttv",
            Global = _ => Succeed(
                EmoteTestData.Entry(id: "54fa8f1401e468494b85b537", code: "KEKW")),
        };
        var sevenTv = new StubEmoteProvider
        {
            Id = "7tv",
            Global = _ => Succeed(
                EmoteTestData.Entry(id: "01GGD5PJA8000FH13S498E9D8X", code: "KEKW")),
        };
        EmoteCatalogService service = Create([bttv, sevenTv]);

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        EmoteCatalogItem item = Assert.Single(result.Entries);
        Assert.Equal("bttv", item.Provider);
        Assert.Equal("54fa8f1401e468494b85b537", item.Entry.Id);
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
            Global = _ => Task.FromResult<IReadOnlyList<EmoteCatalogEntry>>(entries),
        };
        EmoteCatalogService service = Create([provider]);

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.Equal(2_000, result.Entries.Count);
        Assert.Equal("CODE0", result.Entries[0].Entry.Code);
        Assert.Equal("CODE1999", result.Entries[^1].Entry.Code);
    }

    [Fact]
    public async Task Invalid_provider_entries_are_dropped()
    {
        var provider = new StubEmoteProvider
        {
            Global = _ => Succeed(
                EmoteTestData.Entry(id: "not-an-id", code: "BROKEN"),
                EmoteTestData.Entry(code: ""),
                EmoteTestData.Entry(code: "KEKW")),
        };
        EmoteCatalogService service = Create([provider]);

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.Equal("KEKW", Assert.Single(result.Entries).Entry.Code);
    }

    [Fact]
    public async Task An_empty_provider_catalog_is_still_available()
    {
        var provider = new StubEmoteProvider
        {
            Global = _ => Succeed(),
        };
        EmoteCatalogService service = Create([provider]);

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.True(result.Available);
        Assert.False(result.Stale);
        Assert.Equal(Now, result.FetchedAt);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task Channel_entries_precede_global_entries_and_dedupe_by_exact_code()
    {
        var provider = new StubEmoteProvider
        {
            Channel = (_, _) => Succeed(
                EmoteTestData.Entry(id: "aaaaaaaaaaaaaaaaaaaaaaaa", code: "SHARED"),
                EmoteTestData.Entry(id: "bbbbbbbbbbbbbbbbbbbbbbbb", code: "Brazil")),
            Global = _ => Succeed(
                EmoteTestData.Entry(id: "cccccccccccccccccccccccc", code: "SHARED"),
                EmoteTestData.Entry(id: "dddddddddddddddddddddddd", code: "BRAZIL"),
                EmoteTestData.Entry(id: "eeeeeeeeeeeeeeeeeeeeeeee", code: "Brazil")),
        };
        EmoteCatalogService service = Create([provider], new FakeEmoteChannelResolver("42"));

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.Equal(
            ["SHARED", "Brazil", "BRAZIL"],
            result.Entries.Select(item => item.Entry.Code));
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaa", result.Entries[0].Entry.Id);
    }

    [Fact]
    public async Task Channel_catalogs_are_cached_per_channel_id()
    {
        var requested = new List<string>();
        var provider = new StubEmoteProvider
        {
            Channel = (channelId, _) =>
            {
                requested.Add(channelId);
                return Succeed(EmoteTestData.Entry(code: $"CH{channelId}"));
            },
            Global = _ => Succeed(EmoteTestData.Entry(code: "GLOBAL")),
        };
        var resolver = new FakeEmoteChannelResolver
        {
            Resolve = (login, _) => Task.FromResult<string?>(login),
        };
        EmoteCatalogService service = Create([provider], resolver);

        await service.GetCatalogAsync("42", CancellationToken.None);
        await service.GetCatalogAsync("42", CancellationToken.None);
        EmoteCatalogResult other = await service.GetCatalogAsync("43", CancellationToken.None);
        await service.GetCatalogAsync("42", CancellationToken.None);

        Assert.Equal(["42", "43"], requested);
        Assert.Equal(["CH43", "GLOBAL"], other.Entries.Select(item => item.Entry.Code));
        Assert.Equal(1, provider.GlobalCalls);
    }

    [Fact]
    public async Task A_channel_catalog_refreshes_after_five_minutes()
    {
        var call = 0;
        var provider = new StubEmoteProvider
        {
            Channel = (channelId, _) =>
            {
                int next = Interlocked.Increment(ref call);
                return Succeed(EmoteTestData.Entry(code: $"CH{channelId}-{next}"));
            },
        };
        EmoteCatalogService service = Create([provider], new FakeEmoteChannelResolver("42"));

        EmoteCatalogResult first = await service.GetCatalogAsync("chan", CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(5));
        EmoteCatalogResult second = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.Equal("CH42-1", Assert.Single(first.Entries).Entry.Code);
        Assert.Equal("CH42-2", Assert.Single(second.Entries).Entry.Code);
        Assert.Equal(2, provider.ChannelCalls);
        Assert.Equal(Now.AddMinutes(5), second.FetchedAt);
    }

    [Fact]
    public async Task A_failed_channel_refresh_serves_the_stale_channel_and_global_catalogs()
    {
        var fail = false;
        var provider = new StubEmoteProvider
        {
            Channel = (_, _) => fail
                ? Fail(new EmoteProviderException("bttv", 500, "BetterTTV unavailable."))
                : Succeed(EmoteTestData.Entry(code: "CHANNEL")),
            Global = _ => Succeed(EmoteTestData.Entry(code: "GLOBAL")),
        };
        EmoteCatalogService service = Create([provider], new FakeEmoteChannelResolver("42"));

        await service.GetCatalogAsync("chan", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        fail = true;

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.True(result.Available);
        Assert.True(result.Stale);
        Assert.Equal(Now, result.FetchedAt);
        Assert.Equal(["CHANNEL", "GLOBAL"], result.Entries.Select(item => item.Entry.Code));
        Assert.Equal(2, provider.GlobalCalls);
        Assert.Equal(2, provider.ChannelCalls);
        Assert.Single(_logs.Messages);
    }

    [Fact]
    public async Task Each_provider_channel_catalog_is_capped_at_two_thousand_entries()
    {
        EmoteCatalogEntry[] entries = Enumerable.Range(0, 2_001)
            .Select(index => EmoteTestData.Entry(
                id: "aaaaaaaaaaaaaaaaaaaaaaaa", code: $"CODE{index}"))
            .ToArray();
        var provider = new StubEmoteProvider
        {
            Channel = (_, _) => Task.FromResult<IReadOnlyList<EmoteCatalogEntry>>(entries),
        };
        EmoteCatalogService service = Create([provider], new FakeEmoteChannelResolver("42"));

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.Equal(2_000, result.Entries.Count);
        Assert.Equal("CODE0", result.Entries[0].Entry.Code);
        Assert.Equal("CODE1999", result.Entries[^1].Entry.Code);
    }

    [Fact]
    public async Task Concurrent_channel_refreshes_share_one_provider_call()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<EmoteCatalogEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubEmoteProvider
        {
            Channel = (_, _) =>
            {
                started.TrySetResult();
                return release.Task;
            },
        };
        EmoteCatalogService service = Create([provider], new FakeEmoteChannelResolver("42"));

        Task<EmoteCatalogResult> first = service.GetCatalogAsync("chan", CancellationToken.None);
        await started.Task;
        Task<EmoteCatalogResult>[] others = Enumerable.Range(0, 4)
            .Select(_ => service.GetCatalogAsync("chan", CancellationToken.None))
            .ToArray();

        release.SetResult([EmoteTestData.Entry(code: "CHANNEL")]);
        EmoteCatalogResult[] results = await Task.WhenAll([first, .. others]);

        Assert.All(results, result => Assert.Single(result.Entries));
        Assert.Equal(1, provider.GlobalCalls);
        Assert.Equal(1, provider.ChannelCalls);
    }

    [Fact]
    public async Task The_resolved_channel_id_is_passed_to_the_provider()
    {
        string? requestedId = null;
        var provider = new StubEmoteProvider
        {
            Channel = (channelId, _) =>
            {
                requestedId = channelId;
                return Succeed(EmoteTestData.Entry(code: "CHANNEL"));
            },
        };
        EmoteCatalogService service = Create([provider], new FakeEmoteChannelResolver("42"));

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.Equal("42", requestedId);
        Assert.Equal("CHANNEL", Assert.Single(result.Entries).Entry.Code);
    }

    [Fact]
    public async Task An_unresolved_login_serves_global_entries_only()
    {
        var provider = new StubEmoteProvider
        {
            Global = _ => Succeed(EmoteTestData.Entry(code: "GLOBAL")),
            Channel = (_, _) => throw new InvalidOperationException("must not fetch a channel"),
        };
        EmoteCatalogService service = Create([provider]);

        EmoteCatalogResult result = await service.GetCatalogAsync("chan", CancellationToken.None);

        Assert.Equal("GLOBAL", Assert.Single(result.Entries).Entry.Code);
        Assert.Equal(0, provider.ChannelCalls);
    }

    [Fact]
    public async Task A_cold_global_catalog_is_served_when_the_resolver_consumes_the_budget()
    {
        var resolver = new FakeEmoteChannelResolver { Hang = true };
        var provider = new StubEmoteProvider
        {
            Global = _ => Succeed(EmoteTestData.Entry(code: "GLOBAL")),
        };
        EmoteCatalogService service = Create([provider], resolver);

        Task<EmoteCatalogResult> pending = service.GetCatalogAsync(
            "chan", CancellationToken.None);
        await resolver.Started;
        _clock.Advance(TimeSpan.FromSeconds(3));
        EmoteCatalogResult result = await pending;

        Assert.True(result.Available);
        Assert.False(result.Stale);
        Assert.Equal(Now, result.FetchedAt);
        Assert.Equal("GLOBAL", Assert.Single(result.Entries).Entry.Code);
        Assert.Equal(1, provider.GlobalCalls);
        Assert.Equal(0, provider.ChannelCalls);
        AssertResolutionTimeout(_logs.Entries);
    }

    [Fact]
    public async Task Channel_resolution_is_inside_the_three_second_budget()
    {
        var hang = false;
        var resolver = new FakeEmoteChannelResolver
        {
            Resolve = async (_, ct) =>
            {
                if (hang)
                    await EmoteTestData.NeverAsync(ct);

                return "42";
            },
        };
        var provider = new StubEmoteProvider
        {
            Global = _ => Succeed(EmoteTestData.Entry(code: "GLOBAL")),
        };
        EmoteCatalogService service = Create([provider], resolver);

        await service.GetCatalogAsync("chan", CancellationToken.None);
        Assert.Equal(1, provider.GlobalCalls);
        Assert.Equal(1, provider.ChannelCalls);

        hang = true;
        Task<EmoteCatalogResult> pending = service.GetCatalogAsync(
            "chan", CancellationToken.None);
        await resolver.Started;
        _clock.Advance(TimeSpan.FromSeconds(3));
        EmoteCatalogResult result = await pending;

        Assert.True(result.Available);
        Assert.Equal("GLOBAL", Assert.Single(result.Entries).Entry.Code);
        Assert.Equal(1, provider.GlobalCalls);
        AssertResolutionTimeout(_logs.Entries);
    }

    [Fact]
    public async Task A_channel_refresh_timeout_serves_the_stale_catalogs()
    {
        var hang = false;
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubEmoteProvider
        {
            Channel = async (_, ct) =>
            {
                if (!hang)
                    return [EmoteTestData.Entry(code: "CHANNEL")];

                started.TrySetResult();
                await EmoteTestData.NeverAsync(ct);
                throw new InvalidOperationException("unreachable");
            },
            Global = _ => Succeed(EmoteTestData.Entry(code: "GLOBAL")),
        };
        EmoteCatalogService service = Create([provider], new FakeEmoteChannelResolver("42"));

        await service.GetCatalogAsync("chan", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        hang = true;

        Task<EmoteCatalogResult> pending = service.GetCatalogAsync(
            "chan", CancellationToken.None);
        await started.Task;
        _clock.Advance(TimeSpan.FromSeconds(3));
        EmoteCatalogResult result = await pending;

        Assert.True(result.Available);
        Assert.True(result.Stale);
        Assert.Equal(Now, result.FetchedAt);
        Assert.Equal(["CHANNEL", "GLOBAL"], result.Entries.Select(item => item.Entry.Code));
        Assert.Equal(2, provider.GlobalCalls);
        Assert.Equal(2, provider.ChannelCalls);
        AssertRefreshFailure(
            Assert.Single(_logs.Entries), scope: "channel", channelId: "42", httpStatus: null, reason: "timeout");
    }

    [Fact]
    public async Task A_scope_that_crosses_the_ttl_during_another_fetch_is_not_reported_stale()
    {
        var provider = new StubEmoteProvider
        {
            Global = _ => Succeed(EmoteTestData.Entry(code: "GLOBAL")),
            Channel = (_, _) =>
            {
                // The channel fetch finishes after the global catalog's TTL boundary.
                _clock.Advance(TimeSpan.FromSeconds(2));
                return Succeed(EmoteTestData.Entry(code: "CHANNEL"));
            },
        };
        var resolver = new FakeEmoteChannelResolver
        {
            Resolve = (login, _) => Task.FromResult<string?>(login == "chan" ? "42" : null),
        };
        EmoteCatalogService service = Create([provider], resolver);

        // Cache the global catalog while the channel is unresolved.
        await service.GetCatalogAsync("unknown", CancellationToken.None);
        Assert.Equal(1, provider.GlobalCalls);

        // 4:59 later the global catalog is still fresh when it is read; the channel fetch then
        // pushes the clock past 5:00. The global catalog was fresh when served, so not stale.
        _clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        EmoteCatalogResult result = await service.GetCatalogAsync(
            "chan", CancellationToken.None);

        Assert.True(result.Available);
        Assert.False(result.Stale);
        Assert.Equal(Now, result.FetchedAt);
        Assert.Equal(["CHANNEL", "GLOBAL"], result.Entries.Select(item => item.Entry.Code));
        Assert.Equal(1, provider.GlobalCalls);
        Assert.Equal(1, provider.ChannelCalls);
    }

    [Theory]
    [InlineData("invalid-payload")]
    [InlineData("too-large")]
    public async Task Payload_failures_log_a_reason_and_never_a_fake_http_status(string reason)
    {
        EmoteProviderFailureKind kind = reason == "invalid-payload"
            ? EmoteProviderFailureKind.InvalidPayload
            : EmoteProviderFailureKind.TooLarge;
        var provider = new StubEmoteProvider
        {
            Global = _ => Fail(new EmoteProviderException("bttv", null, "bad body", kind)),
        };
        EmoteCatalogService service = Create([provider]);

        await service.GetCatalogAsync("chan", CancellationToken.None);

        AssertRefreshFailure(
            Assert.Single(_logs.Entries), scope: "global", channelId: null, httpStatus: null, reason: reason);
    }

    private static void AssertResolutionTimeout(IReadOnlyList<CapturedLogEntry> entries)
    {
        CapturedLogEntry warning = Assert.Single(
            entries, entry => entry.Template?.Contains("resolution timed out", StringComparison.Ordinal) == true);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("chan", warning.Properties["Login"]);
        Assert.Equal("timeout", warning.Properties["Reason"]);
        Assert.DoesNotContain("=", warning.Template, StringComparison.Ordinal);
    }

    private static void AssertRefreshFailure(
        CapturedLogEntry warning, string scope, string? channelId, int? httpStatus, string reason)
    {
        Assert.Equal("bttv", warning.Properties["ProviderId"]);
        Assert.Equal(scope, warning.Properties["Scope"]);
        Assert.True(warning.Properties.ContainsKey("ChannelId"));
        Assert.Equal(channelId, warning.Properties["ChannelId"]);
        Assert.True(warning.Properties.ContainsKey("HttpStatus"));
        Assert.Equal(httpStatus, warning.Properties["HttpStatus"]);
        Assert.Equal(reason, warning.Properties["Reason"]);
        Assert.DoesNotContain("=", warning.Template, StringComparison.Ordinal);
    }

    private static Task<IReadOnlyList<EmoteCatalogEntry>> Succeed(
        params EmoteCatalogEntry[] entries) =>
        Task.FromResult<IReadOnlyList<EmoteCatalogEntry>>(entries);

    private static Task<IReadOnlyList<EmoteCatalogEntry>> Fail(Exception exception) =>
        Task.FromException<IReadOnlyList<EmoteCatalogEntry>>(exception);

    private EmoteCatalogService Create(
        IEnumerable<IEmoteProvider> providers,
        FakeEmoteChannelResolver? resolver = null) =>
        new(
            providers,
            resolver ?? new FakeEmoteChannelResolver(),
            _clock,
            _loggerFactory.CreateLogger<EmoteCatalogService>());
}
