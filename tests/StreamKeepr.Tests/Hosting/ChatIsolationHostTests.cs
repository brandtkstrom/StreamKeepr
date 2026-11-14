using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Twitch.Chat;

namespace StreamKeepr.Tests.Hosting;

/// <summary>Chat is a best-effort tap: its failures must never reach recording health.</summary>
public sealed class ChatIsolationHostTests
{
    [Fact]
    public async Task A_failing_chat_connection_leaves_the_host_live()
    {
        var sockets = new FailingSocketFactory();
        using var factory = new StreamKeeprAppFactory(
            new Dictionary<string, string?>
            {
                [$"{ChatOptions.SectionName}:Enabled"] = "true",
            },
            configureTestServices: services =>
                services.AddSingleton<IIrcSocketFactory>(sockets));
        using HttpClient client = factory.CreateClient();

        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!factory.Logs.Messages.Any(IsConnectionFailure))
        {
            Assert.True(DateTime.UtcNow < deadline, "The chat client never tried to connect.");
            await Task.Delay(20);
        }

        using HttpResponseMessage live = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.True(sockets.Attempts > 0);
    }

    private static bool IsConnectionFailure(string message) =>
        message.Contains("Twitch IRC connection failed", StringComparison.Ordinal);

    private sealed class FailingSocketFactory : IIrcSocketFactory
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public IIrcSocket Create()
        {
            Interlocked.Increment(ref _attempts);
            return new FailingSocket();
        }
    }

    private sealed class FailingSocket : IIrcSocket
    {
        public Task ConnectAsync(Uri uri, CancellationToken ct) =>
            Task.FromException(new IOException("chat network is down"));

        public Task<IrcSocketFrame> ReceiveAsync(Memory<byte> buffer, CancellationToken ct) =>
            Task.FromException<IrcSocketFrame>(new IOException("chat network is down"));

        public Task SendTextAsync(string text, CancellationToken ct) =>
            Task.FromException(new IOException("chat network is down"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
