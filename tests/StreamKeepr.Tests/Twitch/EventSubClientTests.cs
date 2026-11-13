using System.Net;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Hosting;
using static StreamKeepr.Tests.Twitch.EventSubMessages;

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
    public async Task Conflict_counts_as_covered_when_the_current_session_owns_it()
    {
        int posts = 0;
        _httpHandler.Handler = request =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(SubscriptionsResponse("session-1"));

            Interlocked.Increment(ref posts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
        };

        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);
        Task allConnected = NextStatusAsync(PushStatus.AllConnected);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket!.Push(Welcome("session-1"));
        await allConnected.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(_schedule.IsPushConnected);
        Assert.Equal(2, posts);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Conflict_on_a_stale_session_leaves_the_channel_uncovered()
    {
        _httpHandler.Handler = request =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(SubscriptionsResponse("old-session"));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
        };

        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);
        Task partial = NextStatusAsync(PushStatus.PartialCoverage);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket!.Push(Welcome("session-1"));
        await partial.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(_schedule.IsPushConnected);
        Assert.Single(_socketFactory.Sockets);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Conflict_with_a_disabled_subscription_is_uncovered()
    {
        _httpHandler.Handler = request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"data\":[{\"id\":\"sub-old\",\"status\":\"disabled\","
                        + "\"type\":\"stream.online\",\"condition\":{\"broadcaster_user_id\":\"123\"},"
                        + "\"transport\":{\"method\":\"websocket\",\"session_id\":\"session-1\"}}]}",
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
        };

        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);
        Task partial = NextStatusAsync(PushStatus.PartialCoverage);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket!.Push(Welcome("session-1"));
        await partial.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(_schedule.IsPushConnected);
        Assert.Single(_socketFactory.Sockets);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Conflict_verification_failure_leaves_the_channel_uncovered()
    {
        _httpHandler.Handler = request =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.InternalServerError));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
        };

        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);
        Task partial = NextStatusAsync(PushStatus.PartialCoverage);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket!.Push(Welcome("session-1"));
        await partial.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(_schedule.IsPushConnected);
        Assert.Single(_socketFactory.Sockets);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Conflict_verification_401_refreshes_once_and_retries()
    {
        _httpHandler.Handler = request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                string auth = request.Headers.Authorization?.Parameter ?? "";
                return Task.FromResult(auth == "admin-token"
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : SubscriptionsResponse("session-1"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
        };

        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);
        Task allConnected = NextStatusAsync(PushStatus.AllConnected);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket!.Push(Welcome("session-1"));
        await allConnected.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(_schedule.IsPushConnected);
        Assert.Equal(1, _tokens.RefreshCount);

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

        _socketFactory.LastSocket!.Push(Notification("stream.online", broadcastType: "live"));
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
    public async Task Non_live_online_routes_offline_without_a_channel_lookup()
    {
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
        _socketFactory.LastSocket.Push(Welcome("session-1"));
        await _socketFactory.LastSocket.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _socketFactory.LastSocket.Push(
            Notification("stream.online", messageId: "rerun", broadcastType: "rerun"));

        (_, StreamObservation observation) = await observations.Reader.ReadAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<StreamObservation.Offline>(observation);
        Assert.Equal(0, _poller.ChannelInfoCalls);
        Assert.Single(_socketFactory.Sockets);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Missing_online_type_routes_offline()
    {
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
        _socketFactory.LastSocket.Push(Welcome("session-1"));
        await _socketFactory.LastSocket.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _socketFactory.LastSocket.Push(Notification("stream.online", messageId: "missing"));

        (_, StreamObservation observation) = await observations.Reader.ReadAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<StreamObservation.Offline>(observation);
        Assert.Equal(0, _poller.ChannelInfoCalls);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Dedupe_evicts_the_oldest_message_after_the_capacity()
    {
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
        _socketFactory.LastSocket.Push(Welcome("session-1"));
        await _socketFactory.LastSocket.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (int i = 0; i < 257; i++)
        {
            _socketFactory.LastSocket.Push(
                Notification("stream.offline", messageId: $"id-{i}"));
        }

        for (int i = 0; i < 257; i++)
        {
            await observations.Reader.ReadAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
        }

        _socketFactory.LastSocket.Push(
            Notification("stream.offline", messageId: "id-0"));
        await observations.Reader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(_socketFactory.Sockets);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Duplicate_reconnect_does_not_start_a_second_handoff()
    {
        _httpHandler.Handler = _ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        FakeEventSubSocket oldSocket = await _socketFactory.SocketAsync(1)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await oldSocket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        oldSocket.Push(Welcome("session-1"));
        await oldSocket.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        oldSocket.Push(Reconnect("wss://reconnect.example/one"));
        FakeEventSubSocket newSocket = await _socketFactory.SocketAsync(2)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await newSocket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        oldSocket.Push(Reconnect("wss://reconnect.example/two"));
        newSocket.Push(Welcome("session-2"));
        await oldSocket.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, _socketFactory.Sockets.Count);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Token_change_during_idle_wait_re_evaluates()
    {
        await _channels.RemoveAsync("streamer", CancellationToken.None);
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        await NextStatusAsync(PushStatus.PollingOnly).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(_socketFactory.LastSocket);

        _tokens.Token = null;
        _tokens.RaiseChanged();
        await NextStatusAsync(PushStatus.NoAdminToken).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(_socketFactory.LastSocket);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Voluntary_channel_change_reconnects_without_backoff()
    {
        var jitter = new FixedDelayJitter(TimeSpan.FromSeconds(30));
        EventSubClient client = CreateClient(jitter);
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
    public async Task Token_store_cancellation_does_not_stop_the_loop()
    {
        var store = new OnceCanceledTokenStore();
        EventSubClient client = CreateClient(tokens: store);
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        await store.FirstGet.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _clock.Advance(TimeSpan.FromSeconds(5));

        FakeEventSubSocket socket = await _socketFactory.SocketAsync(1)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await socket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(store.Calls >= 2);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Reconnect_url_query_is_not_logged()
    {
        using var logs = new ListLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        EventSubClient client = CreateClient(
            logger: loggerFactory.CreateLogger<EventSubClient>());
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket.Push(Welcome("session-1"));
        await _socketFactory.LastSocket.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _socketFactory.LastSocket.Push(
            Reconnect("wss://reconnect.example/ws?session_id=secret"));
        await _socketFactory.SocketAsync(2).WaitAsync(TimeSpan.FromSeconds(5));

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        string text = string.Join('\n', logs.Messages);
        Assert.DoesNotContain("session_id=secret", text, StringComparison.Ordinal);
        Assert.Contains("wss://reconnect.example/ws", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_timeout_restarts_the_session()
    {
        _socketFactory.HangOnConnect = true;
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        FakeEventSubSocket first = await _socketFactory.SocketAsync(1)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await first.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _clock.Advance(TimeSpan.FromSeconds(10));
        await _socketFactory.SocketTwo.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(first.Connected.Task.IsCompleted);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Subscription_request_timeout_restarts_the_session()
    {
        var handler = new HangingHandler();
        var factory = new StubHttpClientFactory(handler);
        EventSubClient client = CreateClient(httpFactory: factory);
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        FakeEventSubSocket socket = await _socketFactory.SocketAsync(1)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await socket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        socket.Push(Welcome("session-1"));
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _clock.Advance(TimeSpan.FromSeconds(10));
        await _socketFactory.SocketTwo.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Channel_info_timeout_routes_with_empty_metadata_and_keeps_the_session()
    {
        _poller.HangChannelInfo = true;
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

        FakeEventSubSocket socket = await _socketFactory.SocketAsync(1)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await socket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        socket.Push(Welcome("session-1"));
        await socket.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        socket.Push(Notification("stream.online", messageId: "1", broadcastType: "live"));
        await _poller.LookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _clock.Advance(TimeSpan.FromSeconds(5));

        (_, StreamObservation observation) = await observations.Reader.ReadAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        var live = Assert.IsType<StreamObservation.Live>(observation);
        Assert.Equal("", live.Stream.Title);
        Assert.Equal("", live.Stream.Game);

        socket.Push(Notification("stream.offline", messageId: "2"));
        (_, observation) = await observations.Reader.ReadAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<StreamObservation.Offline>(observation);
        Assert.Single(_socketFactory.Sockets);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Capacity_429_keeps_the_session_and_partial_coverage()
    {
        await _channels.AddAsync(
            new ChannelEntry("second", UserId: "456", DisplayName: "Second"),
            CancellationToken.None);

        int posts = 0;
        var limited = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _httpHandler.Handler = _ =>
        {
            if (Interlocked.Increment(ref posts) == 3)
            {
                limited.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{\"message\":\"capacity\"}"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        };

        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);
        Task partial = NextStatusAsync(PushStatus.PartialCoverage);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket.Push(Welcome("session-1"));
        await limited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await partial.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(_schedule.IsPushConnected);
        Assert.Single(_socketFactory.Sockets);
        Assert.Equal(3, posts);
        Assert.Equal(["streamer"], _schedule.Push.CoveredLogins);
        Assert.Equal(2, _schedule.Push.Watched);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task No_watched_channels_does_not_open_a_socket_and_reports_polling_only()
    {
        await _channels.RemoveAsync("streamer", CancellationToken.None);
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);
        Task pollingOnly = NextStatusAsync(PushStatus.PollingOnly);

        await pollingOnly.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(_socketFactory.LastSocket);

        await _channels.AddAsync(
            new ChannelEntry("late", UserId: "789", DisplayName: "Late"),
            CancellationToken.None);

        FakeEventSubSocket socket = await _socketFactory.SocketAsync(1)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await socket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new Uri("wss://eventsub.wss.twitch.tv/ws"), socket.ConnectedUri);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Unresolved_only_channels_use_partial_coverage_without_a_socket()
    {
        await _channels.RemoveAsync("streamer", CancellationToken.None);
        await _channels.AddAsync(new ChannelEntry("unresolved"), CancellationToken.None);

        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);
        Task partial = NextStatusAsync(PushStatus.PartialCoverage);

        await partial.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(_socketFactory.LastSocket);
        Assert.False(_schedule.Push.HasSession);

        await _channels.SetProfileAsync(
            "unresolved", "999", "Unresolved", null, CancellationToken.None);

        FakeEventSubSocket socket = await _socketFactory.SocketAsync(1)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await socket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Duplicate_notification_id_is_ignored()
    {
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
        _socketFactory.LastSocket.Push(Welcome("session-1"));
        await _socketFactory.LastSocket.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _socketFactory.LastSocket.Push(
            Notification("stream.online", messageId: "same", broadcastType: "live"));
        await observations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        _socketFactory.LastSocket.Push(
            Notification("stream.online", messageId: "same", broadcastType: "live"));
        _socketFactory.LastSocket.Push(Notification("stream.offline", messageId: "other"));

        (_, StreamObservation observation) = await observations.Reader.ReadAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<StreamObservation.Offline>(observation);
        Assert.Single(_socketFactory.Sockets);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Malformed_notification_does_not_restart_the_session()
    {
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
        _socketFactory.LastSocket.Push(Welcome("session-1"));
        await _socketFactory.LastSocket.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _socketFactory.LastSocket.Push(
            "{\"metadata\":{\"message_type\":\"notification\",\"message_id\":\"bad\"},"
            + "\"payload\":{\"subscription\":{\"type\":\"stream.online\"}}}");
        _socketFactory.LastSocket.Push(Notification("stream.offline", messageId: "ok"));

        (_, StreamObservation observation) = await observations.Reader.ReadAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<StreamObservation.Offline>(observation);
        Assert.Single(_socketFactory.Sockets);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Malformed_message_shapes_do_not_restart_the_session()
    {
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
        _socketFactory.LastSocket.Push(Welcome("session-1"));
        await _socketFactory.LastSocket.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _socketFactory.LastSocket.Push("[]");
        _socketFactory.LastSocket.Push("{\"metadata\":\"x\",\"payload\":{}}");
        _socketFactory.LastSocket.Push("{\"metadata\":{\"message_type\":1},\"payload\":{}}");
        _socketFactory.LastSocket.Push(
            "{\"metadata\":{\"message_type\":\"notification\"},\"payload\":[]}");
        _socketFactory.LastSocket.Push(
            "{\"metadata\":{\"message_type\":\"notification\"},"
            + "\"payload\":{\"subscription\":\"x\"}}");
        _socketFactory.LastSocket.Push(Notification("stream.offline", messageId: "ok"));

        (_, StreamObservation observation) = await observations.Reader.ReadAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<StreamObservation.Offline>(observation);
        Assert.Single(_socketFactory.Sockets);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Malformed_reconnect_url_falls_back_to_a_fresh_base_session()
    {
        _httpHandler.Handler = _ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket.Push(Welcome("session-1"));
        await _socketFactory.LastSocket.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _socketFactory.LastSocket.Push(Reconnect("not a url"));
        await _socketFactory.SocketTwo.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            new Uri("wss://eventsub.wss.twitch.tv/ws"),
            _socketFactory.LastSocket!.ConnectedUri);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Route_failure_does_not_restart_the_session()
    {
        var observations = Channel.CreateUnbounded<(string Login, StreamObservation Observation)>();
        int routeCalls = 0;
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync(
            (login, observation, _) =>
            {
                if (Interlocked.Increment(ref routeCalls) == 1)
                    throw new InvalidOperationException("route failed");

                observations.Writer.TryWrite((login, observation));
                return ValueTask.CompletedTask;
            },
            stop.Token);

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket.Push(Welcome("session-1"));
        await _socketFactory.LastSocket.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _socketFactory.LastSocket.Push(
            Notification("stream.online", messageId: "1", broadcastType: "live"));
        _socketFactory.LastSocket.Push(Notification("stream.offline", messageId: "2"));

        (_, StreamObservation observation) = await observations.Reader.ReadAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<StreamObservation.Offline>(observation);
        Assert.Single(_socketFactory.Sockets);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Duplicate_welcome_id_does_not_subscribe_twice()
    {
        int posts = 0;
        var subscribed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _httpHandler.Handler = _ =>
        {
            if (Interlocked.Increment(ref posts) == 2)
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
        _socketFactory.LastSocket.Push(Welcome("session-1", messageId: "welcome-1"));
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _socketFactory.LastSocket.Push(Welcome("session-1", messageId: "welcome-1"));
        _socketFactory.LastSocket.Push(Notification("stream.offline", messageId: "offline-1"));

        await observations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, posts);

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
        oldSocket.Push(Notification("stream.online", broadcastType: "live"));
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

    [Fact]
    public async Task Every_message_including_keepalives_records_the_last_message_time()
    {
        Channel<string> bodies = RecordSubscriptionBodies();
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);
        Assert.Null(_schedule.LastEventSubMessageAt);

        await WelcomeOnSocketAsync(1, "session-1");
        await ReadAsync(bodies, 2);
        Assert.Equal(_clock.GetUtcNow(), _schedule.LastEventSubMessageAt);

        _clock.Advance(TimeSpan.FromSeconds(8));
        FakeEventSubSocket socket = await _socketFactory.SocketAsync(1);
        DateTimeOffset expected = _clock.GetUtcNow();
        socket.Push(
            "{\"metadata\":{\"message_type\":\"session_keepalive\"},\"payload\":{}}");
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (_schedule.LastEventSubMessageAt != expected)
        {
            Assert.True(DateTime.UtcNow < deadline, "keepalive was not recorded");
            await Task.Delay(10);
        }

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Adding_a_channel_subscribes_every_channel_on_the_new_session()
    {
        Channel<string> bodies = RecordSubscriptionBodies();
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        await WelcomeOnSocketAsync(1, "session-1");
        Assert.Equal(["stream.offline/123/session-1", "stream.online/123/session-1"], await ReadAsync(bodies, 2));
        await WaitForPushAsync(PushState.Connected);

        await _channels.AddAsync(
            new ChannelEntry("second", UserId: "456", DisplayName: "Second"), CancellationToken.None);
        await WelcomeOnSocketAsync(2, "session-2");

        // WebSocket subscriptions die with their session, so the new one re-subscribes all channels.
        Assert.Equal(
            [
                "stream.offline/123/session-2", "stream.offline/456/session-2",
                "stream.online/123/session-2", "stream.online/456/session-2",
            ],
            await ReadAsync(bodies, 4));
        await WaitForPushAsync(PushState.Connected);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Removing_a_channel_leaves_it_off_the_new_session()
    {
        await _channels.AddAsync(
            new ChannelEntry("second", UserId: "456", DisplayName: "Second"), CancellationToken.None);
        Channel<string> bodies = RecordSubscriptionBodies();
        EventSubClient client = CreateClient();
        using var stop = new CancellationTokenSource();
        Task run = client.RunAsync((_, _, _) => ValueTask.CompletedTask, stop.Token);

        await WelcomeOnSocketAsync(1, "session-1");
        Assert.Equal(4, (await ReadAsync(bodies, 4)).Length);
        await WaitForPushAsync(PushState.Connected);

        await _channels.RemoveAsync("second", CancellationToken.None);
        await WelcomeOnSocketAsync(2, "session-2");

        Assert.Equal(["stream.offline/123/session-2", "stream.online/123/session-2"], await ReadAsync(bodies, 2));
        await WaitForPushAsync(PushState.Connected);
        Assert.False(bodies.Reader.TryRead(out _));

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Records each subscription request as "type/broadcaster/session" and accepts it.</summary>
    private Channel<string> RecordSubscriptionBodies()
    {
        Channel<string> bodies = Channel.CreateUnbounded<string>();
        _httpHandler.Handler = async request =>
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            bodies.Writer.TryWrite(
                $"{json["type"]}/{json["condition"]!["broadcaster_user_id"]}/{json["transport"]!["session_id"]}");
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        };
        return bodies;
    }

    private static async Task<string[]> ReadAsync(Channel<string> bodies, int count)
    {
        var read = new List<string>();
        for (int i = 0; i < count; i++)
            read.Add(await bodies.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        return [.. read.Order(StringComparer.Ordinal)];
    }

    private async Task WelcomeOnSocketAsync(int number, string sessionId)
    {
        FakeEventSubSocket socket = await _socketFactory.SocketAsync(number).WaitAsync(TimeSpan.FromSeconds(5));
        await socket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        socket.Push(Welcome(sessionId));
    }

    private async Task WaitForPushAsync(PushState expected)
    {
        var met = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (_schedule.Push.State == expected)
                met.TrySetResult();
        }

        _schedule.Changed += Check;
        try
        {
            Check();
            await met.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            _schedule.Changed -= Check;
        }
    }

    private Task NextCoverageAsync()
    {
        var changed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _schedule.Changed += () => changed.TrySetResult();
        return changed.Task;
    }

    private Task NextStatusAsync(PushStatus expected)
    {
        var reached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged()
        {
            if (_schedule.Push.State != expected.State)
                return;

            _schedule.Changed -= OnChanged;
            reached.TrySetResult();
        }

        _schedule.Changed += OnChanged;
        if (_schedule.Push.State == expected.State)
        {
            _schedule.Changed -= OnChanged;
            reached.TrySetResult();
        }

        return reached.Task;
    }

    private EventSubClient CreateClient(
        IRetryJitter? jitter = null,
        IEventSubSocketFactory? socketFactory = null,
        IAdminTokenStore? tokens = null,
        IHttpClientFactory? httpFactory = null,
        ILogger<EventSubClient>? logger = null) =>
        new(
            socketFactory ?? _socketFactory,
            new WebSocketEventSubBinding(
                httpFactory ?? _httpFactory,
                tokens ?? _tokens,
                _channels,
                _schedule,
                Options.Create(new TwitchOptions { ClientId = "client-id" }),
                _clock,
                NullLogger<WebSocketEventSubBinding>.Instance),
            _channels,
            _poller,
            _schedule,
            jitter ?? new NoJitter(),
            _clock,
            logger ?? NullLogger<EventSubClient>.Instance);

    private static HttpResponseMessage SubscriptionsResponse(string sessionId) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"data\":["
                + SubscriptionJson("sub-online", "stream.online", sessionId) + ","
                + SubscriptionJson("sub-offline", "stream.offline", sessionId)
                + "]}",
                Encoding.UTF8,
                "application/json"),
        };

    private static string SubscriptionJson(string id, string type, string sessionId) =>
        "{\"id\":\"" + id + "\",\"status\":\"enabled\",\"type\":\"" + type + "\","
        + "\"condition\":{\"broadcaster_user_id\":\"123\"},"
        + "\"transport\":{\"method\":\"websocket\",\"session_id\":\"" + sessionId + "\"}}";

    private sealed class RecordingJitter : IRetryJitter
    {
        public List<TimeSpan> Bases { get; } = [];

        public TimeSpan Apply(TimeSpan baseDelay)
        {
            Bases.Add(baseDelay);
            return TimeSpan.Zero;
        }
    }

    private sealed class FixedDelayJitter(TimeSpan delay) : IRetryJitter
    {
        public TimeSpan Apply(TimeSpan baseDelay) => delay;
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

    private sealed class HangingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The hanging request completed unexpectedly.");
        }
    }

    private sealed class OnceCanceledTokenStore : IAdminTokenStore
    {
        private int _calls;

        public int Calls => _calls;

        public TaskCompletionSource FirstGet { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action? Changed { add { } remove { } }

        public ValueTask<string?> GetAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstGet.TrySetResult();
                throw new OperationCanceledException("internal token-store timeout");
            }

            return ValueTask.FromResult<string?>("admin-token");
        }

        public ValueTask<string?> RefreshAsync(CancellationToken ct) =>
            ValueTask.FromResult<string?>(null);

        public Task SaveAsync(AdminToken token, CancellationToken ct) => Task.CompletedTask;

        public Task ClearAsync(CancellationToken ct) => Task.CompletedTask;

        public Task WaitForTokenAsync(CancellationToken ct) =>
            Task.Delay(Timeout.InfiniteTimeSpan, ct);
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
}
