using System.Net;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Tests.Twitch;

public sealed class EventSubClientTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(
        new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero));
    private readonly MutableHandler _httpHandler = new();
    private readonly StubHttpClientFactory _httpFactory;
    private readonly ChannelStore _channels;
    private readonly FakeAdminTokenStore _tokens = new("admin-token");
    private readonly FakeEventSubSocketFactory _socketFactory = new();
    private readonly FakeChannelInfoPoller _poller = new();
    private readonly PollSchedule _schedule;

    public EventSubClientTests()
    {
        Directory.CreateDirectory(_root);
        _schedule = new PollSchedule(_clock);
        _channels = new ChannelStore(
            Options.Create(new TwitchOptions()),
            Options.Create(new StorageOptions { RecordingsRoot = _root }),
            NullLogger<ChannelStore>.Instance);
        _channels.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        _channels.AddAsync(
            new ChannelEntry("streamer", UserId: "123", DisplayName: "Streamer"),
            CancellationToken.None).GetAwaiter().GetResult();
        _httpHandler.Handler = _ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Accepted));
        _httpFactory = new StubHttpClientFactory(_httpHandler);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Welcome_subscribes_both_events_and_enables_push_coverage()
    {
        var requests = new List<string>();
        var subscribed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _httpHandler.Handler = async request =>
        {
            string body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync();
            requests.Add(body);
            if (requests.Count == 2)
                subscribed.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        };

        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);
        Task coverageChanged = NextCoverageAsync();

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket!.Push(Welcome("session-1"));
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coverageChanged.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(_schedule.IsPushConnected);
        Assert.Contains(requests, body => body.Contains("stream.online", StringComparison.Ordinal));
        Assert.Contains(requests, body => body.Contains("stream.offline", StringComparison.Ordinal));
        Assert.Contains(requests, body => body.Contains("session-1", StringComparison.Ordinal));
        Assert.Contains(requests, body => body.Contains("123", StringComparison.Ordinal));

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Conflict_counts_as_covered_but_other_refusals_do_not()
    {
        var conflict = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        _httpHandler.Handler = request =>
        {
            if (Interlocked.Increment(ref count) == 2)
                conflict.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
        };

        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);
        Task coverageChanged = NextCoverageAsync();

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket!.Push(Welcome("session-1"));
        await conflict.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coverageChanged.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(_schedule.IsPushConnected);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Refused_subscription_leaves_coverage_false()
    {
        var responded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        _httpHandler.Handler = request =>
        {
            if (Interlocked.Increment(ref count) == 2)
                responded.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(
                count == 1 ? HttpStatusCode.BadRequest : HttpStatusCode.Accepted));
        };

        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);
        Task coverageChanged = NextCoverageAsync();

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket!.Push(Welcome("session-1"));
        await responded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coverageChanged.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(_schedule.IsPushConnected);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Notification_routes_online_and_offline_and_bursts_the_poller()
    {
        _poller.Info = new ChannelInfo("Title", "Game");
        var subscribed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int subscriptions = 0;
        _httpHandler.Handler = request =>
        {
            if (Interlocked.Increment(ref subscriptions) == 2)
                subscribed.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        };

        var observations = Channel.CreateUnbounded<(string Login, StreamObservation Observation)>();
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync(
            (login, observation, _) =>
            {
                observations.Writer.TryWrite((login, observation));
                return ValueTask.CompletedTask;
            },
            stop.Token);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket!.Push(Welcome("session-1"));
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _socketFactory.LastSocket!.Push(Notification("stream.online"));
        (string login, StreamObservation observation) =
            await observations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("streamer", login);
        var live = Assert.IsType<StreamObservation.Live>(observation);
        Assert.Equal("987", live.Stream.StreamId);
        Assert.Equal("Title", live.Stream.Title);
        Assert.Equal("Game", live.Stream.Game);

        _socketFactory.LastSocket!.Push(Notification("stream.offline"));
        (login, observation) = await observations.Reader.ReadAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("streamer", login);
        Assert.IsType<StreamObservation.Offline>(observation);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Unauthorized_subscription_refreshes_once_and_reuses_the_new_token()
    {
        var requests = new List<string>();
        _httpHandler.Handler = request =>
        {
            string token = request.Headers.Authorization?.Parameter ?? "";
            requests.Add(token);
            return Task.FromResult(new HttpResponseMessage(
                requests.Count == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.Accepted));
        };

        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);
        Task coverageChanged = NextCoverageAsync();

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket.Push(Welcome("session-1"));
        await coverageChanged.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(_schedule.IsPushConnected);
        Assert.Equal(1, _tokens.RefreshCount);
        Assert.Equal(["admin-token", "refreshed-token", "refreshed-token"], requests);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Second_unauthorized_does_not_refresh_again_in_the_same_session()
    {
        var tokenStore = new ScriptedTokenStore();
        int requests = 0;
        var secondRequest = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _httpHandler.Handler = _ =>
        {
            if (Interlocked.Increment(ref requests) == 2)
                secondRequest.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        };

        EventSubClient client = CreateClient(tokens: tokenStore);
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket.Push(Welcome("session-1"));
        await secondRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await tokenStore.SecondGet.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, tokenStore.RefreshCount);
        Assert.False(_schedule.IsPushConnected);
        Assert.Single(_socketFactory.Sockets);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Refresh_without_a_token_fails_the_subscription_and_waits_for_a_token()
    {
        _tokens.RefreshedToken = null;
        _httpHandler.Handler = _ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket.Push(Welcome("session-1"));
        await _tokens.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, _tokens.RefreshCount);
        Assert.False(_schedule.IsPushConnected);
        Assert.Single(_socketFactory.Sockets);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Keepalive_silence_reconnects()
    {
        var subscribed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int subscriptions = 0;
        _httpHandler.Handler = _ =>
        {
            if (Interlocked.Increment(ref subscriptions) == 2)
                subscribed.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        };
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket.Push(Welcome("session-1", keepaliveSeconds: 1));
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _clock.Advance(TimeSpan.FromSeconds(11));
        await _socketFactory.SocketTwo.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Reconnect_message_uses_the_supplied_url()
    {
        _httpHandler.Handler = _ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket.Push(Welcome("session-1"));
        await _socketFactory.LastSocket.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _socketFactory.LastSocket.Push(Reconnect("wss://reconnect.example/ws"));
        await _socketFactory.SocketTwo.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _socketFactory.LastSocket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            new Uri("wss://reconnect.example/ws"),
            _socketFactory.LastSocket.ConnectedUri);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Reconnect_keeps_the_old_socket_until_the_new_welcome_and_keeps_subscriptions()
    {
        var subscriptionBodies = new List<string>();
        var subscribed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _httpHandler.Handler = async request =>
        {
            string body = await request.Content!.ReadAsStringAsync();
            lock (subscriptionBodies)
            {
                subscriptionBodies.Add(body);
                if (subscriptionBodies.Count == 2)
                    subscribed.TrySetResult();
            }

            return new HttpResponseMessage(HttpStatusCode.Accepted);
        };

        var observations = Channel.CreateUnbounded<StreamObservation>();
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync(
            (_, observation, _) =>
            {
                observations.Writer.TryWrite(observation);
                return ValueTask.CompletedTask;
            },
            stop.Token);

        FakeEventSubSocket oldSocket = await _socketFactory.SocketAsync(1)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await oldSocket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        oldSocket.Push(Welcome("session-1"));
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        oldSocket.Push(Reconnect("wss://reconnect.example/ws"));
        FakeEventSubSocket newSocket = await _socketFactory.SocketAsync(2)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await newSocket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Twitch keeps delivering on the old socket until the new one says welcome.
        Assert.False(oldSocket.Disposed.Task.IsCompleted, "old socket closed before the new welcome");
        Assert.True(_schedule.IsPushConnected);
        oldSocket.Push(Notification("stream.online"));
        Assert.IsType<StreamObservation.Live>(
            await observations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(oldSocket.Disposed.Task.IsCompleted, "old socket closed before the new welcome");

        newSocket.Push(Welcome("session-2"));
        await oldSocket.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Messages are handled in order, so a re-subscribe on the new welcome would
        // have happened before this notification is routed.
        newSocket.Push(Notification("stream.offline"));
        Assert.IsType<StreamObservation.Offline>(
            await observations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        lock (subscriptionBodies)
        {
            Assert.Equal(2, subscriptionBodies.Count);
            Assert.DoesNotContain(
                subscriptionBodies, body => body.Contains("session-2", StringComparison.Ordinal));
        }

        Assert.True(_schedule.IsPushConnected);
        Assert.False(newSocket.Disposed.Task.IsCompleted);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Reconnect_socket_closing_before_welcome_falls_back_to_a_fresh_session()
    {
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        FakeEventSubSocket oldSocket = await _socketFactory.SocketAsync(1)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await oldSocket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        oldSocket.Push(Welcome("session-1"));
        await oldSocket.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        oldSocket.Push(Reconnect("wss://reconnect.example/ws"));
        FakeEventSubSocket newSocket = await _socketFactory.SocketAsync(2)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await newSocket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        newSocket.Close();

        FakeEventSubSocket fresh = await _socketFactory.SocketAsync(3)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await fresh.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new Uri("wss://eventsub.wss.twitch.tv/ws"), fresh.ConnectedUri);
        Assert.True(oldSocket.Disposed.Task.IsCompleted);
        Assert.True(newSocket.Disposed.Task.IsCompleted);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Reconnect_without_a_new_welcome_in_30_seconds_falls_back_to_a_fresh_session()
    {
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        FakeEventSubSocket oldSocket = await _socketFactory.SocketAsync(1)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await oldSocket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        oldSocket.Push(Welcome("session-1"));
        await oldSocket.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        oldSocket.Push(Reconnect("wss://reconnect.example/ws"));
        FakeEventSubSocket newSocket = await _socketFactory.SocketAsync(2)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await newSocket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _clock.Advance(TimeSpan.FromSeconds(31));

        FakeEventSubSocket fresh = await _socketFactory.SocketAsync(3)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await fresh.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new Uri("wss://eventsub.wss.twitch.tv/ws"), fresh.ConnectedUri);
        Assert.True(oldSocket.Disposed.Task.IsCompleted);
        Assert.True(newSocket.Disposed.Task.IsCompleted);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Revocation_logs_and_reconnects_with_backoff()
    {
        _httpHandler.Handler = _ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket.Push(Welcome("session-1"));
        await _socketFactory.LastSocket.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _socketFactory.LastSocket.Push(Revoked());
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await _socketFactory.SocketTwo.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Token_change_restarts_the_session()
    {
        _httpHandler.Handler = _ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket.Push(Welcome("session-1"));
        await _socketFactory.LastSocket.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _tokens.RaiseChanged();
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await _socketFactory.SocketTwo.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Reconnects_back_off_exponentially_with_jitter()
    {
        var jitter = new RecordingJitter();
        var sockets = new ClosingSocketFactory();
        EventSubClient client = CreateClient(jitter, sockets);

        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        await sockets.Third.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [5, 10, 20],
            jitter.Bases.Take(3).Select(delay => (int)delay.TotalSeconds));
    }

    [Fact]
    public async Task Channel_change_restarts_the_session()
    {
        _httpHandler.Handler = _ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket.Push(Welcome("session-1"));
        await _socketFactory.LastSocket.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await _channels.AddAsync(
            new ChannelEntry("second", UserId: "456", DisplayName: "Second"),
            CancellationToken.None);

        await _socketFactory.SocketTwo.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Failed_subscription_reconnects_and_leaves_coverage_false()
    {
        _httpHandler.Handler = _ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket.Push(Welcome("session-1"));

        await _socketFactory.SocketTwo.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(_schedule.IsPushConnected);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Without_a_token_it_waits_and_keeps_push_coverage_off()
    {
        _tokens.Token = null;
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        await _tokens.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(_schedule.IsPushConnected);
        Assert.Null(_socketFactory.LastSocket);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private Task NextCoverageAsync()
    {
        var changed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _schedule.Changed += () => changed.TrySetResult();
        return changed.Task;
    }

    private EventSubClient CreateClient(
        IRetryJitter? jitter = null,
        IEventSubSocketFactory? socketFactory = null,
        IAdminTokenStore? tokens = null) =>
        new(
            socketFactory ?? _socketFactory,
            _httpFactory,
            tokens ?? _tokens,
            _channels,
            _poller,
            _schedule,
            jitter ?? new NoJitter(),
            Options.Create(new TwitchOptions { ClientId = "client-id" }),
            _clock,
            NullLogger<EventSubClient>.Instance);

    private static string Welcome(string sessionId, int keepaliveSeconds = 10) =>
        "{\"metadata\":{\"message_type\":\"session_welcome\"},"
        + "\"payload\":{\"session\":{\"id\":\"" + sessionId + "\","
        + "\"keepalive_timeout_seconds\":" + keepaliveSeconds + "}}}";

    private static string Reconnect(string url) =>
        "{\"metadata\":{\"message_type\":\"session_reconnect\"},"
        + "\"payload\":{\"session\":{\"reconnect_url\":\"" + url + "\"}}}";

    private static string Revoked() =>
        "{\"metadata\":{\"message_type\":\"revocation\"},\"payload\":{}}";

    private static string Notification(string type) =>
        "{\"metadata\":{\"message_type\":\"notification\"},"
        + "\"payload\":{\"subscription\":{\"type\":\"" + type + "\"},"
        + "\"event\":{\"id\":\"987\",\"broadcaster_user_id\":\"123\","
        + "\"started_at\":\"2026-11-10T16:30:00Z\"}}}";

    private sealed class NoJitter : IRetryJitter
    {
        public TimeSpan Apply(TimeSpan baseDelay) => TimeSpan.Zero;
    }

    private sealed class RecordingJitter : IRetryJitter
    {
        public List<TimeSpan> Bases { get; } = [];

        public TimeSpan Apply(TimeSpan baseDelay)
        {
            Bases.Add(baseDelay);
            return TimeSpan.Zero;
        }
    }

    private sealed class ClosingSocketFactory : IEventSubSocketFactory
    {
        public TaskCompletionSource Third { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _created;

        public IEventSubSocket Create()
        {
            if (Interlocked.Increment(ref _created) == 3)
                Third.TrySetResult();
            return new ClosingSocket();
        }
    }

    private sealed class ClosingSocket : IEventSubSocket
    {
        public Task ConnectAsync(Uri uri, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> ReceiveTextAsync(CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MutableHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Handler { get; set; } =
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Handler(request);
    }

    private sealed class FakeChannelInfoPoller : IStreamPoller
    {
        public ChannelInfo? Info { get; set; }

        public Task RunAsync(
            Func<IReadOnlyList<string>> logins,
            StreamObservationRoute route,
            CancellationToken ct) =>
            Task.Delay(Timeout.InfiniteTimeSpan, ct);

        public Task<ChannelProfile?> GetUserAsync(string login, CancellationToken ct) =>
            Task.FromResult<ChannelProfile?>(null);

        public Task<ChannelInfo?> GetChannelInfoAsync(
            string broadcasterId, CancellationToken ct) =>
            Task.FromResult(Info);

        public void Nudge()
        {
        }
    }

    private sealed class ScriptedTokenStore : IAdminTokenStore
    {
        private int _getCalls;

        public int RefreshCount { get; private set; }

        public TaskCompletionSource SecondGet { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action? Changed { add { } remove { } }

        public ValueTask<string?> GetAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _getCalls) == 1)
                return ValueTask.FromResult<string?>("admin-token");

            SecondGet.TrySetResult();
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask<string?> RefreshAsync(CancellationToken ct)
        {
            RefreshCount++;
            return ValueTask.FromResult<string?>("refreshed-token");
        }

        public Task SaveAsync(AdminToken token, CancellationToken ct) => Task.CompletedTask;

        public Task ClearAsync(CancellationToken ct) => Task.CompletedTask;

        public Task WaitForTokenAsync(CancellationToken ct) =>
            Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private sealed class FakeAdminTokenStore(string? token) : IAdminTokenStore
    {
        public string? Token { get; set; } = token;

        public TaskCompletionSource Waiting { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action? Changed;

        public string? RefreshedToken { get; set; } = "refreshed-token";

        public int RefreshCount { get; private set; }

        public ValueTask<string?> GetAsync(CancellationToken ct) =>
            ValueTask.FromResult(Token);

        public ValueTask<string?> RefreshAsync(CancellationToken ct)
        {
            RefreshCount++;
            Token = RefreshedToken;
            return ValueTask.FromResult(RefreshedToken);
        }

        public Task SaveAsync(AdminToken token, CancellationToken ct)
        {
            Token = token.AccessToken;
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct)
        {
            Token = null;
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public async Task WaitForTokenAsync(CancellationToken ct)
        {
            Waiting.TrySetResult();
            if (Token is null)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        public void RaiseChanged() => Changed?.Invoke();
    }

    private sealed class FakeEventSubSocketFactory : IEventSubSocketFactory
    {
        private readonly List<TaskCompletionSource<FakeEventSubSocket>> _slots = [];

        public List<FakeEventSubSocket> Sockets { get; } = [];

        public FakeEventSubSocket? LastSocket => Sockets.LastOrDefault();

        public TaskCompletionSource SocketTwo { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _created;

        public IEventSubSocket Create()
        {
            var socket = new FakeEventSubSocket();
            Sockets.Add(socket);
            int created = Interlocked.Increment(ref _created);
            Slot(created).TrySetResult(socket);
            if (created == 2)
                SocketTwo.TrySetResult();
            return socket;
        }

        /// <summary>Completes when the <paramref name="number"/>th socket (1-based) is created.</summary>
        public Task<FakeEventSubSocket> SocketAsync(int number) => Slot(number).Task;

        private TaskCompletionSource<FakeEventSubSocket> Slot(int number)
        {
            lock (_slots)
            {
                while (_slots.Count < number)
                {
                    _slots.Add(new TaskCompletionSource<FakeEventSubSocket>(
                        TaskCreationOptions.RunContinuationsAsynchronously));
                }

                return _slots[number - 1];
            }
        }
    }

    private sealed class FakeEventSubSocket : IEventSubSocket
    {
        private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
        private int _received;

        public TaskCompletionSource Connected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Subscribed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Uri? ConnectedUri { get; private set; }

        public Task ConnectAsync(Uri uri, CancellationToken ct)
        {
            ConnectedUri = uri;
            Connected.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveTextAsync(CancellationToken ct)
        {
            if (!await _messages.Reader.WaitToReadAsync(ct)
                || !_messages.Reader.TryRead(out string? message))
            {
                return null;
            }

            if (Interlocked.Increment(ref _received) == 1)
                Subscribed.TrySetResult();
            return message;
        }

        public void Push(string message) => _messages.Writer.TryWrite(message);

        /// <summary>Simulates Twitch closing the socket: pending and later receives return null.</summary>
        public void Close() => _messages.Writer.TryComplete();

        public ValueTask DisposeAsync()
        {
            _messages.Writer.TryComplete();
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
