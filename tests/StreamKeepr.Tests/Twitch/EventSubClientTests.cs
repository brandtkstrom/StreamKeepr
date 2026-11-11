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

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket!.Push(Welcome("session-1"));
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

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

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket!.Push(Welcome("session-1"));
        await conflict.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

        await _socketFactory.LastSocket!.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _socketFactory.LastSocket!.Push(Welcome("session-1"));
        await responded.Task.WaitAsync(TimeSpan.FromSeconds(5));

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

    private EventSubClient CreateClient() =>
        new(
            _socketFactory,
            _httpFactory,
            _tokens,
            _channels,
            _poller,
            _schedule,
            Options.Create(new TwitchOptions { ClientId = "client-id" }),
            _clock,
            NullLogger<EventSubClient>.Instance);

    private static string Welcome(string sessionId) =>
        "{\"metadata\":{\"message_type\":\"session_welcome\"},"
        + "\"payload\":{\"session\":{\"id\":\"" + sessionId + "\"}}}";

    private static string Notification(string type) =>
        "{\"metadata\":{\"message_type\":\"notification\"},"
        + "\"payload\":{\"subscription\":{\"type\":\"" + type + "\"},"
        + "\"event\":{\"id\":\"987\",\"broadcaster_user_id\":\"123\","
        + "\"started_at\":\"2026-11-10T16:30:00Z\"}}}";

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

    private sealed class FakeAdminTokenStore(string? token) : IAdminTokenStore
    {
        public string? Token { get; set; } = token;

        public TaskCompletionSource Waiting { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action? Changed;

        public ValueTask<string?> GetAsync(CancellationToken ct) =>
            ValueTask.FromResult(Token);

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
    }

    private sealed class FakeEventSubSocketFactory : IEventSubSocketFactory
    {
        public FakeEventSubSocket? LastSocket { get; private set; }

        public TaskCompletionSource Subscribed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IEventSubSocket Create() =>
            LastSocket = new FakeEventSubSocket(Subscribed);
    }

    private sealed class FakeEventSubSocket : IEventSubSocket
    {
        private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource _subscribed;
        private int _received;

        public FakeEventSubSocket(TaskCompletionSource subscribed) => _subscribed = subscribed;

        public TaskCompletionSource Connected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ConnectAsync(Uri uri, CancellationToken ct)
        {
            Connected.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveTextAsync(CancellationToken ct)
        {
            string message = await _messages.Reader.ReadAsync(ct);
            if (Interlocked.Increment(ref _received) == 1)
                _subscribed.TrySetResult();
            return message;
        }

        public void Push(string message) => _messages.Writer.TryWrite(message);

        public ValueTask DisposeAsync()
        {
            _messages.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
