using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace StreamKeepr.Infrastructure.Twitch;

public sealed class ClientWebSocketEventSubSocketFactory(
    TimeProvider clock,
    ILogger<ClientWebSocketEventSubSocket> logger) : IEventSubSocketFactory
{
    public IEventSubSocket Create() => new ClientWebSocketEventSubSocket(clock, logger);
}

/// <summary>Production EventSub socket over <see cref="ClientWebSocket"/>.</summary>
public sealed class ClientWebSocketEventSubSocket(
    TimeProvider clock,
    ILogger<ClientWebSocketEventSubSocket> logger) : IEventSubSocket
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

    private readonly ClientWebSocket _socket = new();

    public Task ConnectAsync(Uri uri, CancellationToken ct) =>
        _socket.ConnectAsync(uri, ct);

    public async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];

        while (true)
        {
            WebSocketReceiveResult result = await _socket.ReceiveAsync(
                new ArraySegment<byte>(buffer), ct);

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                while (!result.EndOfMessage)
                {
                    result = await _socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), ct);
                }

                continue;
            }

            using var message = new MemoryStream();
            while (true)
            {
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    break;

                result = await _socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), ct);
            }

            return Encoding.UTF8.GetString(message.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var closeTimeout = new CancellationTokenSource(CloseTimeout, clock);
            try
            {
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "shutdown", closeTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogDebug(
                    "EventSub socket close timed out; aborting the connection.");
                _socket.Abort();
            }
            catch (WebSocketException)
            {
                // The peer may already be gone.
                _socket.Abort();
            }
        }

        _socket.Dispose();
    }
}
