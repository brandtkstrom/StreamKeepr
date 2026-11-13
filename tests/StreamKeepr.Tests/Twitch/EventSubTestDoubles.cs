using System.Threading.Channels;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Tests.Twitch;

// Test doubles shared by the WebSocket and conduit EventSub tests.

/// <summary>EventSub WebSocket message builders.</summary>
internal static class EventSubMessages
{
    public static string Welcome(
        string sessionId, int keepaliveSeconds = 10, string? messageId = null) =>
        "{\"metadata\":{\"message_type\":\"session_welcome\""
        + (messageId is null ? "" : ",\"message_id\":\"" + messageId + "\"")
        + "},\"payload\":{\"session\":{\"id\":\"" + sessionId + "\","
        + "\"keepalive_timeout_seconds\":" + keepaliveSeconds + "}}}";

    public static string Reconnect(string url) =>
        "{\"metadata\":{\"message_type\":\"session_reconnect\"},"
        + "\"payload\":{\"session\":{\"reconnect_url\":\"" + url + "\"}}}";

    public static string Revoked() =>
        "{\"metadata\":{\"message_type\":\"revocation\"},\"payload\":{}}";

    public static string Notification(
        string type, string? messageId = null, string? broadcastType = null) =>
        "{\"metadata\":{\"message_type\":\"notification\""
        + (messageId is null ? "" : ",\"message_id\":\"" + messageId + "\"")
        + "},\"payload\":{\"subscription\":{\"type\":\"" + type + "\"},"
        + "\"event\":{\"id\":\"987\",\"broadcaster_user_id\":\"123\","
        + (broadcastType is null ? "" : "\"type\":\"" + broadcastType + "\",")
        + "\"started_at\":\"2026-11-10T16:30:00Z\"}}}";
}

internal sealed class NoJitter : IRetryJitter
{
    public TimeSpan Apply(TimeSpan baseDelay) => TimeSpan.Zero;
}

internal sealed class FakeChannelInfoPoller : IStreamPoller
{
    public ChannelInfo? Info { get; set; }

    public int ChannelInfoCalls { get; private set; }

    public TaskCompletionSource LookupStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool HangChannelInfo { get; set; }

    public Task RunAsync(
        Func<IReadOnlyList<string>> logins,
        StreamObservationRoute route,
        CancellationToken ct) =>
        Task.Delay(Timeout.InfiniteTimeSpan, ct);

    public Task<ChannelProfile?> GetUserAsync(string login, CancellationToken ct) =>
        Task.FromResult<ChannelProfile?>(null);

    public async Task<ChannelInfo?> GetChannelInfoAsync(
        string broadcasterId, CancellationToken ct)
    {
        ChannelInfoCalls++;
        LookupStarted.TrySetResult();
        if (HangChannelInfo)
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);

        return Info;
    }

    public void Nudge()
    {
    }
}

internal sealed class FakeEventSubSocketFactory : IEventSubSocketFactory
{
    private readonly List<TaskCompletionSource<FakeEventSubSocket>> _slots = [];

    public List<FakeEventSubSocket> Sockets { get; } = [];

    public FakeEventSubSocket? LastSocket => Sockets.LastOrDefault();

    public TaskCompletionSource SocketTwo { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool HangOnConnect { get; set; }

    private int _created;

    public IEventSubSocket Create()
    {
        var socket = new FakeEventSubSocket { HangOnConnect = HangOnConnect };
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

internal sealed class FakeEventSubSocket : IEventSubSocket
{
    private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
    private int _received;

    public TaskCompletionSource Connected { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ConnectStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Subscribed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Disposed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Uri? ConnectedUri { get; private set; }

    public bool HangOnConnect { get; set; }

    public Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        ConnectedUri = uri;
        ConnectStarted.TrySetResult();
        if (HangOnConnect)
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);

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
