using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;
using static StreamKeepr.Tests.Twitch.EventSubMessages;

namespace StreamKeepr.Tests.Twitch;

/// <summary><see cref="EventSubClient"/> driven by <see cref="ConduitEventSubBinding"/> against a fake Twitch.</summary>
public sealed class ConduitEventSubClientTests : IDisposable
{
    private const string ClientId = "client-id";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(
        new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeConduitTwitch _twitch = new();
    private readonly FakeEventSubSocketFactory _sockets = new();
    private readonly FakeChannelInfoPoller _poller = new();
    private readonly ChannelStore _channels;
    private readonly PollSchedule _schedule;
    private readonly ConduitStateStore _state;
    private readonly TwitchTokenProvider _tokens;
    private readonly IOptions<TwitchOptions> _options = Options.Create(new TwitchOptions
    {
        ClientId = ClientId,
        ClientSecret = "secret",
        EventSubTransport = EventSubTransportMode.Conduit,
    });

    public ConduitEventSubClientTests()
    {
        Directory.CreateDirectory(_root);
        _schedule = new PollSchedule(_clock);
        var storage = Options.Create(new StorageOptions { RecordingsRoot = _root });
        _channels = new ChannelStore(
            Options.Create(new TwitchOptions()), storage, NullLogger<ChannelStore>.Instance);
        _channels.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        AddChannel("streamer", "123");
        _state = new ConduitStateStore(storage, NullLogger<ConduitStateStore>.Instance);
        _tokens = new TwitchTokenProvider(new StubHttpClientFactory(_twitch), _options, _clock);
    }

    public void Dispose()
    {
        _tokens.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task First_start_creates_and_saves_a_conduit_binds_the_shard_then_subscribes()
    {
        await using Running run = Start();

        await WelcomeAsync("session-1");
        await WaitForPushAsync(PushState.Connected);

        string conduit = Assert.Single(_twitch.Conduits);
        Assert.Equal(new ConduitState(conduit, ClientId), await _state.LoadAsync(CancellationToken.None));
        Assert.Equal("session-1", _twitch.ShardSessions[conduit]);
        Assert.Equal(
            ["stream.offline/123", "stream.online/123"],
            _twitch.Subscriptions.Select(s => $"{s.Type}/{s.UserId}").Order());
        Assert.All(_twitch.Subscriptions, s => Assert.Equal(conduit, s.ConduitId));

        // The shard must be bound within 10 s of the welcome, so it goes before reconciling.
        int patch = _twitch.Requests.FindIndex(r => r.Method == HttpMethod.Patch);
        int firstCreate = _twitch.Requests.FindIndex(
            r => r.Method == HttpMethod.Post && r.Path == "helix/eventsub/subscriptions");
        Assert.InRange(patch, 0, firstCreate - 1);
        Assert.True(_schedule.IsPushConnected);
    }

    [Fact]
    public async Task Restart_reuses_the_saved_conduit_and_creates_only_what_is_missing()
    {
        string conduit = _twitch.AddConduit();
        _twitch.Add("stream.online", "123", conduit);
        await _state.SaveAsync(new ConduitState(conduit, ClientId), CancellationToken.None);

        await using Running run = Start();
        await WelcomeAsync("session-1");
        await WaitForPushAsync(PushState.Connected);

        Assert.Equal([conduit], _twitch.Conduits);
        Assert.Empty(_twitch.RequestsMatching(HttpMethod.Post, "helix/eventsub/conduits"));
        FakeRequest create = Assert.Single(
            _twitch.RequestsMatching(HttpMethod.Post, "helix/eventsub/subscriptions"));
        Assert.Equal("stream.offline", create.Json!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_saved_conduit_that_twitch_deleted_is_replaced()
    {
        await _state.SaveAsync(new ConduitState("expired-conduit", ClientId), CancellationToken.None);

        await using Running run = Start();
        await WelcomeAsync("session-1");
        await WaitForPushAsync(PushState.Connected);

        string conduit = Assert.Single(_twitch.Conduits);
        Assert.NotEqual("expired-conduit", conduit);
        Assert.Equal(conduit, (await _state.LoadAsync(CancellationToken.None))!.ConduitId);
    }

    [Fact]
    public async Task A_conduit_saved_under_another_client_id_is_left_alone()
    {
        string foreign = _twitch.AddConduit();
        await _state.SaveAsync(new ConduitState(foreign, "old-client"), CancellationToken.None);

        await using Running run = Start();
        await WelcomeAsync("session-1");
        await WaitForPushAsync(PushState.Connected);

        Assert.Equal(2, _twitch.Conduits.Count);
        Assert.Contains(foreign, _twitch.Conduits);
        Assert.Empty(_twitch.RequestsMatching(HttpMethod.Delete, "helix/eventsub/conduits"));
        Assert.Equal(ClientId, (await _state.LoadAsync(CancellationToken.None))!.ClientId);
    }

    [Fact]
    public async Task Unwatched_and_disabled_subscriptions_are_deleted_and_the_gap_recreated()
    {
        string conduit = _twitch.AddConduit();
        FakeSubscription unwatched = _twitch.Add("stream.online", "999", conduit);
        FakeSubscription revoked = _twitch.Add("stream.online", "123", conduit, "authorization_revoked");
        _twitch.Add("stream.offline", "123", conduit);
        await _state.SaveAsync(new ConduitState(conduit, ClientId), CancellationToken.None);

        await using Running run = Start();
        await WelcomeAsync("session-1");
        await WaitForPushAsync(PushState.Connected);

        Assert.Equal(
            [unwatched.Id, revoked.Id],
            _twitch.RequestsMatching(HttpMethod.Delete, "helix/eventsub/subscriptions")
                .Select(r => r.Query["id"]));
        Assert.Equal(
            ["stream.offline/123", "stream.online/123"],
            _twitch.Subscriptions.Select(s => $"{s.Type}/{s.UserId}").Order());
    }

    [Fact]
    public async Task Adding_a_channel_subscribes_it_and_keeps_existing_subscriptions()
    {
        await using Running run = Start();
        await WelcomeAsync("session-1");
        await WaitForPushAsync(PushState.Connected);
        string[] before = _twitch.Subscriptions.Select(s => s.Id).ToArray();

        AddChannel("second", "456");
        await WelcomeAsync("session-2", socketNumber: 2);
        await WaitForPushAsync(PushState.Connected);

        Assert.Equal(
            ["stream.offline/123", "stream.offline/456", "stream.online/123", "stream.online/456"],
            _twitch.Subscriptions.Select(s => $"{s.Type}/{s.UserId}").Order());
        Assert.Subset(_twitch.Subscriptions.Select(s => s.Id).ToHashSet(), before.ToHashSet());
        Assert.Empty(_twitch.RequestsMatching(HttpMethod.Delete, "helix/eventsub/subscriptions"));
        Assert.Equal("session-2", _twitch.ShardSessions[Assert.Single(_twitch.Conduits)]);
    }

    [Fact]
    public async Task Removing_a_channel_deletes_only_its_subscriptions()
    {
        AddChannel("second", "456");
        await using Running run = Start();
        await WelcomeAsync("session-1");
        await WaitForPushAsync(PushState.Connected);
        int createsBefore = _twitch.RequestsMatching(HttpMethod.Post, "helix/eventsub/subscriptions").Length;

        await _channels.RemoveAsync("second", CancellationToken.None);
        await WelcomeAsync("session-2", socketNumber: 2);
        await WaitForPushAsync(PushState.Connected);

        Assert.Equal(
            ["stream.offline/123", "stream.online/123"],
            _twitch.Subscriptions.Select(s => $"{s.Type}/{s.UserId}").Order());
        Assert.Equal(2, _twitch.RequestsMatching(HttpMethod.Delete, "helix/eventsub/subscriptions").Length);
        Assert.Equal(
            createsBefore,
            _twitch.RequestsMatching(HttpMethod.Post, "helix/eventsub/subscriptions").Length);
    }

    [Fact]
    public async Task Removing_the_last_channel_deletes_everything_and_falls_back_to_polling()
    {
        await using Running run = Start();
        await WelcomeAsync("session-1");
        await WaitForPushAsync(PushState.Connected);

        await _channels.RemoveAsync("streamer", CancellationToken.None);
        await WelcomeAsync("session-2", socketNumber: 2);
        await WaitForPushAsync(PushState.PollingOnly);

        Assert.Empty(_twitch.Subscriptions);
        Assert.Single(_twitch.Conduits);
    }

    [Fact]
    public async Task A_channel_is_subscribed_once_its_user_id_resolves()
    {
        await using Running run = Start();
        await WelcomeAsync("session-1");
        await WaitForPushAsync(PushState.Connected);

        // Added by login only: not coverable until Helix resolves the user ID.
        await _channels.AddAsync("second", CancellationToken.None);
        await WelcomeAsync("session-2", socketNumber: 2);
        await WaitForPushAsync(PushState.PartialCoverage);
        Assert.DoesNotContain(_twitch.Subscriptions, s => s.UserId == "456");

        await _channels.SetProfileAsync("second", "456", "Second", null, CancellationToken.None);
        await WelcomeAsync("session-3", socketNumber: 3);
        await WaitForPushAsync(PushState.Connected);

        Assert.Equal(
            ["stream.offline/456", "stream.online/456"],
            _twitch.Subscriptions.Where(s => s.UserId == "456").Select(s => $"{s.Type}/{s.UserId}").Order());
    }

    [Fact]
    public async Task A_refused_channel_leaves_partial_coverage()
    {
        AddChannel("other", "456");
        _twitch.RefusedUserIds.Add("456");

        await using Running run = Start();
        await WelcomeAsync("session-1");
        await WaitForPushAsync(PushState.PartialCoverage);

        Assert.False(_schedule.IsPushConnected);
        Assert.Equal(["streamer"], _schedule.Push.CoveredLogins);
        Assert.Equal(2, _schedule.Push.Watched);
    }

    [Fact]
    public async Task A_conflict_counts_only_when_the_subscription_is_enabled_on_our_conduit()
    {
        _twitch.Override = request => request.Method == HttpMethod.Post
            && request.RequestUri!.AbsolutePath.EndsWith("subscriptions", StringComparison.Ordinal)
                ? FakeConduitTwitch.Error(HttpStatusCode.Conflict, "subscription already exists")
                : null;

        await using Running run = Start();
        await WelcomeAsync("session-1");
        await WaitForPushAsync(PushState.PartialCoverage);

        // The 409 was checked against a second listing of our conduit.
        Assert.Equal(2, _twitch.RequestsMatching(HttpMethod.Get, "helix/eventsub/subscriptions").Length);
    }

    [Fact]
    public async Task A_conflict_for_a_subscription_already_on_our_conduit_is_covered()
    {
        string conduit = _twitch.AddConduit();
        await _state.SaveAsync(new ConduitState(conduit, ClientId), CancellationToken.None);
        _twitch.Override = request =>
        {
            if (request.Method != HttpMethod.Post
                || !request.RequestUri!.AbsolutePath.EndsWith("subscriptions", StringComparison.Ordinal))
            {
                return null;
            }

            // Another writer created it after our first listing.
            string type = request.Content!.ReadAsStringAsync().Result.Contains("stream.online", StringComparison.Ordinal)
                ? "stream.online"
                : "stream.offline";
            _twitch.Add(type, "123", conduit);
            return FakeConduitTwitch.Error(HttpStatusCode.Conflict, "subscription already exists");
        };

        await using Running run = Start();
        await WelcomeAsync("session-1");
        await WaitForPushAsync(PushState.Connected);
    }

    [Fact]
    public async Task Notifications_route_to_the_channel_and_burst_the_poller()
    {
        var routed = new TaskCompletionSource<StreamObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Running run = Start((login, observation, _) =>
        {
            if (login == "streamer")
                routed.TrySetResult(observation);
            return ValueTask.CompletedTask;
        });
        await WelcomeAsync("session-1");
        await WaitForPushAsync(PushState.Connected);

        (await _sockets.SocketAsync(1)).Push(Notification("stream.offline"));

        Assert.IsType<StreamObservation.Offline>(await routed.Task.WaitAsync(Wait));
    }

    [Fact]
    public async Task Revocation_marks_partial_coverage_without_reconnecting()
    {
        var routed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Running run = Start((_, _, _) =>
        {
            routed.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await WelcomeAsync("session-1");
        await WaitForPushAsync(PushState.Connected);

        FakeEventSubSocket socket = await _sockets.SocketAsync(1);
        socket.Push(ConduitRevoked("stream.online", "123"));
        await WaitForPushAsync(PushState.PartialCoverage);
        Assert.Empty(_schedule.Push.CoveredLogins);
        Assert.Equal(1, _schedule.Push.Watched);

        // A later message on the same socket proves the session kept running.
        socket.Push(Notification("stream.offline"));
        await routed.Task.WaitAsync(Wait);
        Assert.False(_sockets.SocketAsync(2).IsCompleted);
    }

    [Fact]
    public async Task Revocation_without_a_broadcaster_trusts_no_coverage_until_the_next_session()
    {
        AddChannel("other", "456");
        await using Running run = Start();
        await WelcomeAsync("session-1");
        await WaitForPushAsync(PushState.Connected);

        FakeEventSubSocket socket = await _sockets.SocketAsync(1);
        socket.Push(
            "{\"metadata\":{\"message_type\":\"revocation\"},"
            + "\"payload\":{\"subscription\":{\"type\":\"stream.online\",\"status\":\"authorization_revoked\"}}}");
        await WaitForPushAsync(PushState.PartialCoverage);

        Assert.Empty(_schedule.Push.CoveredLogins);
        Assert.Equal(2, _schedule.Push.Watched);
        Assert.True(_schedule.Push.HasSession);
    }

    [Fact]
    public async Task Revocation_for_an_unwatched_channel_keeps_coverage()
    {
        var routed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Running run = Start((_, _, _) =>
        {
            routed.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await WelcomeAsync("session-1");
        await WaitForPushAsync(PushState.Connected);

        FakeEventSubSocket socket = await _sockets.SocketAsync(1);
        socket.Push(ConduitRevoked("stream.online", "999"));
        socket.Push(Notification("stream.offline"));
        await routed.Task.WaitAsync(Wait);

        Assert.True(_schedule.IsPushConnected);
        Assert.Equal(["streamer"], _schedule.Push.CoveredLogins);
    }

    [Fact]
    public async Task Reconnect_rebinds_the_shard_to_the_new_session_before_closing_the_old_socket()
    {
        await using Running run = Start();
        await WelcomeAsync("session-1");
        await WaitForPushAsync(PushState.Connected);
        FakeEventSubSocket old = await _sockets.SocketAsync(1);
        var rebound = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _twitch.ShardAssigned += session =>
        {
            if (session == "session-2")
                rebound.TrySetResult(old.Disposed.Task.IsCompleted);
        };

        old.Push(Reconnect("wss://eventsub.wss.twitch.tv/ws?reconnect"));
        FakeEventSubSocket replacement = await _sockets.SocketAsync(2).WaitAsync(Wait);
        await replacement.Connected.Task.WaitAsync(Wait);
        replacement.Push(Welcome("session-2"));

        bool oldClosedFirst = await rebound.Task.WaitAsync(Wait);
        await old.Disposed.Task.WaitAsync(Wait);

        Assert.False(oldClosedFirst);
        Assert.Equal("session-2", _twitch.ShardSessions[Assert.Single(_twitch.Conduits)]);
        Assert.Equal(2, _twitch.RequestsMatching(HttpMethod.Post, "helix/eventsub/subscriptions").Length);
        Assert.True(_schedule.IsPushConnected);
    }

    [Fact]
    public async Task A_failed_shard_assignment_restarts_the_session()
    {
        _twitch.Override = request => request.Method == HttpMethod.Patch
            ? FakeConduitTwitch.Error(HttpStatusCode.BadRequest, "websocket session not found")
            : null;

        await using Running run = Start();
        await WelcomeAsync("session-1");

        await _sockets.SocketAsync(2).WaitAsync(Wait);
        Assert.False(_schedule.IsPushConnected);
        Assert.Empty(_twitch.RequestsMatching(HttpMethod.Post, "helix/eventsub/subscriptions"));
    }

    [Fact]
    public async Task A_twitch_auth_outage_backs_off_without_opening_a_socket()
    {
        int tokenRequests = 0;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _twitch.Override = request =>
        {
            if (request.RequestUri!.AbsolutePath != "/oauth2/token")
                return null;
            if (Interlocked.Increment(ref tokenRequests) == 2)
                secondAttempt.TrySetResult();
            return FakeConduitTwitch.Error(HttpStatusCode.ServiceUnavailable, "down");
        };

        var jitter = new PassThroughJitter();
        await using Running run = Start(jitter: jitter);

        Assert.Equal(TimeSpan.FromSeconds(5), await jitter.FirstBase.Task.WaitAsync(Wait));
        Assert.Equal(PushState.EventSubReconnecting, _schedule.Push.State);
        Assert.Equal(1, Volatile.Read(ref tokenRequests));

        // The backoff timer starts just after the jitter call; advance until it fires.
        for (int i = 0; i < 100 && !secondAttempt.Task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        await secondAttempt.Task.WaitAsync(Wait);
        Assert.False(_sockets.SocketAsync(1).IsCompleted);
    }

    [Fact]
    public async Task Binding_never_restarts_for_admin_sign_in_or_logout()
    {
        ConduitEventSubBinding binding = CreateBinding();
        int restarts = 0;
        void Count() => restarts++;

        binding.RestartRequested += Count;
        binding.RestartRequested -= Count;

        Assert.Equal(0, restarts);
        Assert.DoesNotContain(
            typeof(ConduitEventSubBinding).GetConstructors().Single().GetParameters(),
            p => p.ParameterType == typeof(IAdminTokenStore));
    }

    private static string ConduitRevoked(string type, string userId) =>
        "{\"metadata\":{\"message_type\":\"revocation\"},"
        + "\"payload\":{\"subscription\":{\"id\":\"sub-x\",\"status\":\"authorization_revoked\","
        + "\"type\":\"" + type + "\",\"condition\":{\"broadcaster_user_id\":\"" + userId + "\"},"
        + "\"transport\":{\"method\":\"conduit\",\"conduit_id\":\"c\"}}}}";

    private void AddChannel(string login, string userId) =>
        _channels.AddAsync(new ChannelEntry(login, UserId: userId, DisplayName: login), CancellationToken.None)
            .GetAwaiter().GetResult();

    private async Task WelcomeAsync(string sessionId, int socketNumber = 1)
    {
        FakeEventSubSocket socket = await _sockets.SocketAsync(socketNumber).WaitAsync(Wait);
        await socket.Connected.Task.WaitAsync(Wait);
        socket.Push(Welcome(sessionId));
    }

    private Task WaitForPushAsync(PushState expected) =>
        WaitUntilAsync(() => _schedule.Push.State == expected);

    private async Task WaitUntilAsync(Func<bool> condition)
    {
        var met = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (condition())
                met.TrySetResult();
        }

        _schedule.Changed += Check;
        try
        {
            Check();
            await met.Task.WaitAsync(Wait);
        }
        finally
        {
            _schedule.Changed -= Check;
        }
    }

    private ConduitEventSubBinding CreateBinding() =>
        new(
            new EventSubConduitApi(
                new StubHttpClientFactory(_twitch), _tokens, _options, NullLogger<EventSubConduitApi>.Instance),
            _state,
            _channels,
            _schedule,
            _options,
            NullLogger<ConduitEventSubBinding>.Instance);

    private Running Start(StreamObservationRoute? route = null, IRetryJitter? jitter = null)
    {
        var client = new EventSubClient(
            _sockets,
            CreateBinding(),
            _channels,
            _poller,
            _schedule,
            jitter ?? new NoJitter(),
            _clock,
            NullLogger<EventSubClient>.Instance);
        var stop = new CancellationTokenSource();
        Task task = client.RunAsync(route ?? ((_, _, _) => ValueTask.CompletedTask), stop.Token);
        return new Running(task, stop);
    }

    private sealed class Running(Task task, CancellationTokenSource stop) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await task.WaitAsync(Wait);
            stop.Dispose();
        }
    }

    private sealed class PassThroughJitter : IRetryJitter
    {
        public TaskCompletionSource<TimeSpan> FirstBase { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan Apply(TimeSpan baseDelay)
        {
            FirstBase.TrySetResult(baseDelay);
            return baseDelay;
        }
    }
}
