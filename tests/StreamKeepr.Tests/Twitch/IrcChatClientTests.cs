using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch.Chat;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Twitch;

public sealed class IrcChatClientTests : IAsyncLifetime
{
    private const string Welcome = ":tmi.twitch.tv 001 justinfan12345 :Welcome, GLHF!";

    private readonly FakeTimeProvider _clock = new(
        new DateTimeOffset(2026, 11, 13, 18, 0, 0, TimeSpan.Zero));
    private readonly FakeSocketFactory _sockets = new();
    private readonly ListLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggers;
    private readonly ConcurrentQueue<ChatSourceEvent> _received = new();
    private readonly CancellationTokenSource _reading = new();
    private IrcChatClient? _client;

    public IrcChatClientTests() =>
        _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _reading.CancelAsync();
        if (_client is not null)
        {
            await _client.StopAsync(CancellationToken.None);
            await _client.DisposeAsync();
        }

        _reading.Dispose();
        _loggers.Dispose();
    }

    [Fact]
    public async Task Registers_receives_a_privmsg_and_answers_ping()
    {
        await StartAsync(new ChatOptions { Enabled = true }, "chan");
        FakeSocket socket = await _sockets.WaitForAsync(1);

        socket.Enqueue(":tmi.twitch.tv CAP * ACK :twitch.tv/tags twitch.tv/commands");
        socket.Enqueue(Welcome);
        socket.Enqueue("PING :tmi.twitch.tv");
        socket.Enqueue(
            "@id=abc;tmi-sent-ts=1507246572675;user-id=42 " +
            ":alice!alice@alice.tmi.twitch.tv PRIVMSG #chan :hello");

        ChatSourceEvent.MessageEvent message =
            await NextAsync<ChatSourceEvent.MessageEvent>();

        Assert.Equal("hello", message.Message.Text);
        Assert.Equal("chan", message.Message.Channel);
        Assert.Contains(socket.Sent, line => line.StartsWith("NICK justinfan", StringComparison.Ordinal));
        Assert.Contains(socket.Sent, line => line.StartsWith("USER ", StringComparison.Ordinal));
        Assert.Contains(socket.Sent, line => line.StartsWith("CAP REQ ", StringComparison.Ordinal));
        Assert.Contains(socket.Sent, line => line.StartsWith("PONG :tmi.twitch.tv", StringComparison.Ordinal));
        Assert.DoesNotContain(socket.Sent, line => line.StartsWith("PASS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reconnect_request_rejoins_at_once_and_reports_the_outage()
    {
        await StartAsync(new ChatOptions { Enabled = true }, "chan");
        FakeSocket first = await _sockets.WaitForAsync(1);
        first.Enqueue(Welcome);
        await NextAsync<ChatSourceEvent.Joined>();

        first.Enqueue(":tmi.twitch.tv RECONNECT");
        ChatSourceEvent.Disconnected disconnected =
            await NextAsync<ChatSourceEvent.Disconnected>();
        FakeSocket second = await _sockets.WaitForAsync(2);
        second.Enqueue(Welcome);
        ChatSourceEvent.Joined rejoined = await NextAsync<ChatSourceEvent.Joined>();

        Assert.Equal("chan", disconnected.Channel);
        Assert.Equal("chan", rejoined.Channel);
        Assert.Contains("JOIN #chan", second.Sent);
    }

    [Fact]
    public async Task A_rejected_token_falls_back_to_anonymous_capture()
    {
        await StartAsync(new ChatOptions
        {
            Enabled = true,
            TwitchUserOAuthToken = "token-value",
            TwitchLogin = "keeper",
        }, "chan");
        FakeSocket first = await _sockets.WaitForAsync(1);
        first.Enqueue(":tmi.twitch.tv NOTICE * :Login authentication failed");

        FakeSocket second = await _sockets.WaitForAsync(2);

        Assert.Contains("PASS oauth:token-value", first.Sent);
        Assert.Contains("NICK keeper", first.Sent);
        Assert.DoesNotContain(second.Sent, line => line.StartsWith("PASS", StringComparison.Ordinal));
        Assert.Contains(second.Sent, line => line.StartsWith("NICK justinfan", StringComparison.Ordinal));
        Assert.Contains(_logs.Messages, message => message.Contains(
            "Falling back to anonymous", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_nick_collision_on_an_anonymous_login_reconnects_with_a_new_nick()
    {
        await StartAsync(new ChatOptions { Enabled = true }, "chan");
        FakeSocket first = await _sockets.WaitForAsync(1);
        first.Enqueue(":tmi.twitch.tv 433 * justinfan12345 :Nickname is already in use");

        FakeSocket second = await _sockets.WaitForAsync(2);

        Assert.Contains(second.Sent, line => line.StartsWith("NICK justinfan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_capability_nak_waits_a_minute_before_retrying()
    {
        await StartAsync(new ChatOptions { Enabled = true }, "chan");
        FakeSocket first = await _sockets.WaitForAsync(1);
        first.Enqueue(":tmi.twitch.tv CAP * NAK :twitch.tv/tags twitch.tv/commands");
        await EventuallyAsync(() => _logs.Messages.Any(message => message.Contains(
            "rejected the required IRC capabilities", StringComparison.Ordinal)));

        _clock.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(50);
        Assert.Equal(1, _sockets.CreatedCount);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await _sockets.WaitForAsync(2);
    }

    [Fact]
    public async Task The_join_cap_refuses_channels_beyond_the_limit()
    {
        await StartAsync(new ChatOptions { Enabled = true, MaxChannels = 1 }, "first", "second");
        FakeSocket socket = await _sockets.WaitForAsync(1);
        socket.Enqueue(Welcome);
        await NextAsync<ChatSourceEvent.Joined>();

        _clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.Contains("JOIN #first", socket.Sent);
        Assert.DoesNotContain("JOIN #second", socket.Sent);
        Assert.Contains(_logs.Messages, message => message.Contains(
            "join limit of 1 channels reached", StringComparison.Ordinal));
    }

    private async Task StartAsync(ChatOptions options, params string[] channels)
    {
        _client = new IrcChatClient(
            _sockets,
            new TwitchChatParser(_clock),
            Options.Create(options),
            new FixedJitter(),
            _clock,
            _loggers.CreateLogger<IrcChatClient>());
        foreach (string channel in channels)
            await _client.JoinAsync(channel, CancellationToken.None);

        await _client.StartAsync(CancellationToken.None);
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (ChatSourceEvent evt in _client.ReadAllAsync(_reading.Token))
                    _received.Enqueue(evt);
            }
            catch (OperationCanceledException)
            {
                // The test is over.
            }
        });
    }

    /// <summary>Takes received events up to and including the first of the wanted type.</summary>
    private async Task<T> NextAsync<T>() where T : ChatSourceEvent
    {
        T? found = null;
        await EventuallyAsync(() =>
        {
            while (found is null && _received.TryDequeue(out ChatSourceEvent? evt))
                found = evt as T;
            return found is not null;
        });
        return found!;
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold in time.");
            await Task.Delay(10);
        }
    }

    private sealed class FixedJitter : IRetryJitter
    {
        public TimeSpan Apply(TimeSpan baseDelay) => baseDelay;
    }

    private sealed class FakeSocketFactory : IIrcSocketFactory
    {
        private readonly ConcurrentQueue<FakeSocket> _created = new();

        public int CreatedCount => _created.Count;

        public IIrcSocket Create()
        {
            var socket = new FakeSocket();
            _created.Enqueue(socket);
            return socket;
        }

        /// <summary>The <paramref name="count"/>th socket the client created (1-based).</summary>
        public async Task<FakeSocket> WaitForAsync(int count)
        {
            await EventuallyAsync(() => _created.Count >= count);
            return _created.ElementAt(count - 1);
        }
    }

    private sealed class FakeSocket : IIrcSocket
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
        private readonly ConcurrentQueue<string> _sent = new();

        public IReadOnlyList<string> Sent => _sent.ToArray();

        public void Enqueue(string line) => _incoming.Writer.TryWrite(line);

        public Task ConnectAsync(Uri uri, CancellationToken ct) => Task.CompletedTask;

        public async Task<IrcSocketFrame> ReceiveAsync(
            Memory<byte> buffer, CancellationToken ct)
        {
            if (!await _incoming.Reader.WaitToReadAsync(ct))
                return new IrcSocketFrame(0, Closed: true);

            if (!_incoming.Reader.TryRead(out string? line))
                return new IrcSocketFrame(0, Closed: false);

            byte[] bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            bytes.CopyTo(buffer.Span);
            return new IrcSocketFrame(bytes.Length, Closed: false);
        }

        public Task SendTextAsync(string text, CancellationToken ct)
        {
            _sent.Enqueue(text.TrimEnd('\r', '\n'));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
