using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Twitch;

public sealed class ConduitRetirementTests : IDisposable
{
    private const string ClientId = "client-id";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeConduitTwitch _twitch = new();
    private readonly ConduitStateStore _state;
    private readonly List<TwitchTokenProvider> _tokenProviders = [];

    public ConduitRetirementTests()
    {
        _state = new ConduitStateStore(
            Options.Create(new StorageOptions { RecordingsRoot = _root }),
            NullLogger<ConduitStateStore>.Instance);
    }

    public void Dispose()
    {
        foreach (TwitchTokenProvider tokens in _tokenProviders)
            tokens.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task WebSocket_mode_deletes_our_leftover_conduit_and_forgets_it()
    {
        string conduit = _twitch.AddConduit();
        await _state.SaveAsync(new ConduitState(conduit, ClientId), CancellationToken.None);

        await CreateRetirement(EventSubTransportMode.WebSocket).RetireAsync(CancellationToken.None);

        Assert.Empty(_twitch.Conduits);
        Assert.Null(await _state.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_delete_keeps_the_state_so_the_next_start_retries()
    {
        string conduit = _twitch.AddConduit();
        await _state.SaveAsync(new ConduitState(conduit, ClientId), CancellationToken.None);
        _twitch.Override = request => request.Method == HttpMethod.Delete
            ? FakeConduitTwitch.Error(HttpStatusCode.ServiceUnavailable, "down")
            : null;

        await CreateRetirement(EventSubTransportMode.WebSocket).RetireAsync(CancellationToken.None);

        Assert.Equal(new ConduitState(conduit, ClientId), await _state.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_conduit_from_another_client_id_is_forgotten_without_a_delete()
    {
        string conduit = _twitch.AddConduit();
        await _state.SaveAsync(new ConduitState(conduit, "old-client"), CancellationToken.None);

        await CreateRetirement(EventSubTransportMode.WebSocket).RetireAsync(CancellationToken.None);

        Assert.Equal([conduit], _twitch.Conduits);
        Assert.Empty(_twitch.Requests);
        Assert.Null(await _state.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Conduit_mode_keeps_the_conduit()
    {
        string conduit = _twitch.AddConduit();
        await _state.SaveAsync(new ConduitState(conduit, ClientId), CancellationToken.None);

        await CreateRetirement(EventSubTransportMode.Conduit).RetireAsync(CancellationToken.None);

        Assert.Equal([conduit], _twitch.Conduits);
        Assert.Empty(_twitch.Requests);
        Assert.NotNull(await _state.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Without_saved_state_nothing_is_called()
    {
        await CreateRetirement(EventSubTransportMode.WebSocket).RetireAsync(CancellationToken.None);

        Assert.Empty(_twitch.Requests);
    }

    [Theory]
    [InlineData(null, typeof(WebSocketEventSubBinding))]
    [InlineData("WebSocket", typeof(WebSocketEventSubBinding))]
    [InlineData("Conduit", typeof(ConduitEventSubBinding))]
    public void Host_selects_the_binding_from_the_transport_flag(string? transport, Type expected)
    {
        using var root = new TempRecordingsRoot();
        var overrides = new Dictionary<string, string?>();
        if (transport is not null)
            overrides["Twitch:EventSubTransport"] = transport;

        // Keep the real client from reaching Twitch while the host runs.
        using var factory = new StreamKeeprAppFactory(
            overrides,
            root.Path,
            services => services.AddSingleton<IEventSubClient, IdleEventSubClient>());
        using HttpClient client = factory.CreateClient();

        Assert.IsType(expected, factory.Services.GetRequiredService<IEventSubBinding>());
        Assert.Contains(
            factory.Services.GetServices<IHostedService>(),
            service => service is ConduitRetirementService);
    }

    private ConduitRetirementService CreateRetirement(EventSubTransportMode mode)
    {
        var options = Options.Create(new TwitchOptions
        {
            ClientId = ClientId,
            ClientSecret = "secret",
            EventSubTransport = mode,
        });
        var factory = new StubHttpClientFactory(_twitch);
        var tokens = new TwitchTokenProvider(
            factory, options, new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero)));
        _tokenProviders.Add(tokens);
        return new ConduitRetirementService(
            options,
            _state,
            new EventSubConduitApi(factory, tokens, options, NullLogger<EventSubConduitApi>.Instance),
            NullLogger<ConduitRetirementService>.Instance);
    }

    private sealed class IdleEventSubClient : IEventSubClient
    {
        public string Transport => "Test";

        public Task RunAsync(StreamObservationRoute route, CancellationToken ct) =>
            Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }
}
