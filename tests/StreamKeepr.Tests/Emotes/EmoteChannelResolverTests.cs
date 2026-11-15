using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Emotes;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Hosting;
using StreamKeepr.Tests.Runtime;

namespace StreamKeepr.Tests.Emotes;

public sealed class EmoteChannelResolverTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly ScriptedStreamPoller _poller = new();
    private readonly ListLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;

    public EmoteChannelResolverTests()
    {
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(_logs));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private ILogger<EmoteChannelResolver> Logger =>
        _loggerFactory.CreateLogger<EmoteChannelResolver>();

    [Fact]
    public async Task A_profile_in_the_channel_store_wins_without_a_poller_lookup()
    {
        ChannelStore store = await CreateStoreAsync("chan");
        await store.SetProfileAsync(
            "chan", "42", "Chan", avatarUrl: null, CancellationToken.None);
        _poller.Lookup = (_, _) => Task.FromResult<ChannelProfile?>(
            new ChannelProfile("99", "Chan", null));
        EmoteChannelResolver resolver = new(store, _poller, _clock, Logger);

        string? userId = await resolver.ResolveAsync(" CHAN ", CancellationToken.None);

        Assert.Equal("42", userId);
        Assert.Equal(0, _poller.Lookups);
    }

    [Fact]
    public async Task A_missing_profile_is_resolved_by_the_poller_and_cached_for_five_minutes()
    {
        ChannelStore store = await CreateStoreAsync();
        _poller.Lookup = (_, _) => Task.FromResult<ChannelProfile?>(
            new ChannelProfile("42", "Chan", null));
        EmoteChannelResolver resolver = new(store, _poller, _clock, Logger);

        Assert.Equal("42", await resolver.ResolveAsync("chan", CancellationToken.None));
        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal("42", await resolver.ResolveAsync("chan", CancellationToken.None));
        Assert.Equal(1, _poller.Lookups);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("42", await resolver.ResolveAsync("chan", CancellationToken.None));
        Assert.Equal(2, _poller.Lookups);
    }

    [Fact]
    public async Task An_unknown_login_caches_the_missing_resolution_for_five_minutes()
    {
        ChannelStore store = await CreateStoreAsync();
        EmoteChannelResolver resolver = new(store, _poller, _clock, Logger);

        Assert.Null(await resolver.ResolveAsync("missing", CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync("missing", CancellationToken.None));
        Assert.Equal(1, _poller.Lookups);

        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(await resolver.ResolveAsync("missing", CancellationToken.None));
        Assert.Equal(2, _poller.Lookups);
    }

    [Fact]
    public async Task A_transient_poller_failure_is_not_cached()
    {
        ChannelStore store = await CreateStoreAsync();
        var failed = false;
        _poller.Lookup = (_, _) =>
        {
            if (failed)
            {
                return Task.FromResult<ChannelProfile?>(
                    new ChannelProfile("42", "Chan", null));
            }

            failed = true;
            return Task.FromException<ChannelProfile?>(
                new HttpRequestException("Twitch unavailable."));
        };
        EmoteChannelResolver resolver = new(store, _poller, _clock, Logger);

        Assert.Null(await resolver.ResolveAsync("chan", CancellationToken.None));
        Assert.Equal("42", await resolver.ResolveAsync("chan", CancellationToken.None));
        Assert.Equal(2, _poller.Lookups);
        string warning = Assert.Single(_logs.Messages);
        Assert.Contains("login=chan", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_numeric_poller_id_is_treated_as_unresolved()
    {
        ChannelStore store = await CreateStoreAsync();
        _poller.Lookup = (_, _) => Task.FromResult<ChannelProfile?>(
            new ChannelProfile("not-a-number", "Chan", null));
        EmoteChannelResolver resolver = new(store, _poller, _clock, Logger);

        Assert.Null(await resolver.ResolveAsync("chan", CancellationToken.None));
    }

    [Fact]
    public async Task A_store_resolution_is_cached_for_five_minutes()
    {
        ChannelStore store = await CreateStoreAsync("chan");
        await store.SetProfileAsync(
            "chan", "42", "Chan", avatarUrl: null, CancellationToken.None);
        EmoteChannelResolver resolver = new(store, _poller, _clock, Logger);

        Assert.Equal("42", await resolver.ResolveAsync("chan", CancellationToken.None));

        await store.SetProfileAsync(
            "chan", "43", "Chan", avatarUrl: null, CancellationToken.None);
        Assert.Equal("42", await resolver.ResolveAsync("chan", CancellationToken.None));
        Assert.Equal(0, _poller.Lookups);

        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("43", await resolver.ResolveAsync("chan", CancellationToken.None));
        Assert.Equal(0, _poller.Lookups);
    }

    [Fact]
    public async Task Concurrent_resolutions_share_one_poller_lookup()
    {
        ChannelStore store = await CreateStoreAsync();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ChannelProfile?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _poller.Lookup = (_, _) =>
        {
            started.TrySetResult();
            return release.Task;
        };
        EmoteChannelResolver resolver = new(store, _poller, _clock, Logger);

        Task<string?> first = resolver.ResolveAsync("chan", CancellationToken.None);
        await started.Task;
        Task<string?>[] others = Enumerable.Range(0, 4)
            .Select(_ => resolver.ResolveAsync("chan", CancellationToken.None))
            .ToArray();

        release.SetResult(new ChannelProfile("42", "Chan", null));
        string?[] results = await Task.WhenAll([first, .. others]);

        Assert.All(results, userId => Assert.Equal("42", userId));
        Assert.Equal(1, _poller.Lookups);
    }

    private async Task<ChannelStore> CreateStoreAsync(params string[] seeds)
    {
        var store = new ChannelStore(
            Options.Create(new TwitchOptions { Channels = [.. seeds] }),
            Options.Create(new StorageOptions { RecordingsRoot = _root }),
            NullLogger<ChannelStore>.Instance);
        await store.LoadAsync(CancellationToken.None);
        return store;
    }
}
