using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Twitch.Chat;

namespace StreamKeepr.Tests.Twitch;

public sealed class IrcChatClientTests
{
    [Fact]
    public async Task Registers_receives_a_privmsg_and_answers_ping()
    {
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 11, 13, 18, 0, 0, TimeSpan.Zero));
        var factory = new FakeSocketFactory();
        var client = new IrcChatClient(
            factory,
            new TwitchChatParser(clock),
            Options.Create(new ChatOptions { Enabled = true }),
            new FixedJitter(),
            clock,
            NullLogger<IrcChatClient>.Instance);

        await client.JoinAsync("chan", CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        factory.Socket.Enqueue(":tmi.twitch.tv CAP * ACK :twitch.tv/tags twitch.tv/commands");
        factory.Socket.Enqueue(":tmi.twitch.tv 001 justinfan12345 :Welcome, GLHF!");
        factory.Socket.Enqueue("PING :tmi.twitch.tv");
        factory.Socket.Enqueue(
            "@id=abc;tmi-sent-ts=1507246572675;user-id=42 " +
            ":alice!alice@alice.tmi.twitch.tv PRIVMSG #chan :hello");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ChatSourceEvent.MessageEvent? message = null;
        await foreach (ChatSourceEvent evt in client.ReadAllAsync(timeout.Token))
        {
            if (evt is ChatSourceEvent.MessageEvent found)
            {
                message = found;
                break;
            }
        }

        Assert.NotNull(message);
        Assert.Equal("hello", message.Message.Text);
        Assert.Equal("chan", message.Message.Channel);
        Assert.Contains(factory.Socket.Sent, line => line.StartsWith("NICK ", StringComparison.Ordinal));
        Assert.Contains(factory.Socket.Sent, line => line.StartsWith("USER ", StringComparison.Ordinal));
        Assert.Contains(factory.Socket.Sent, line => line.StartsWith("CAP REQ ", StringComparison.Ordinal));
        Assert.Contains(factory.Socket.Sent, line => line.StartsWith("PONG :tmi.twitch.tv", StringComparison.Ordinal));

        await client.StopAsync(CancellationToken.None);
        await client.DisposeAsync();
    }

    private sealed class FixedJitter : IRetryJitter
    {
        public TimeSpan Apply(TimeSpan baseDelay) => baseDelay;
    }

    private sealed class FakeSocketFactory : IIrcSocketFactory
    {
        public FakeSocket Socket { get; } = new();

        public IIrcSocket Create() => this.Socket;
    }

    private sealed class FakeSocket : IIrcSocket
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();

        public List<string> Sent { get; } = [];

        public void Enqueue(string line) => this._incoming.Writer.TryWrite(line);

        public Task ConnectAsync(Uri uri, CancellationToken ct) => Task.CompletedTask;

        public async Task<IrcSocketFrame> ReceiveAsync(
            Memory<byte> buffer, CancellationToken ct)
        {
            if (!await this._incoming.Reader.WaitToReadAsync(ct))
                return new IrcSocketFrame(0, Closed: true);

            if (!this._incoming.Reader.TryRead(out string? line))
                return new IrcSocketFrame(0, Closed: false);

            byte[] bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            bytes.CopyTo(buffer.Span);
            return new IrcSocketFrame(bytes.Length, Closed: false);
        }

        public Task SendTextAsync(string text, CancellationToken ct)
        {
            this.Sent.Add(text.TrimEnd('\r', '\n'));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            this._incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
