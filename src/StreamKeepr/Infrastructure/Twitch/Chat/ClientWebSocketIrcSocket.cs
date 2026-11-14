using System.Net.WebSockets;
using System.Text;

namespace StreamKeepr.Infrastructure.Twitch.Chat;

public sealed class ClientWebSocketIrcSocketFactory : IIrcSocketFactory
{
    public IIrcSocket Create() => new ClientWebSocketIrcSocket();
}

/// <summary>Production IRC-over-WebSocket socket. The line reader owns framing.</summary>
public sealed class ClientWebSocketIrcSocket : IIrcSocket
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

    private readonly ClientWebSocket _socket = new();

    public async Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        this._socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await this._socket.ConnectAsync(uri, ct);
    }

    public async Task<IrcSocketFrame> ReceiveAsync(
        Memory<byte> buffer, CancellationToken ct)
    {
        ValueWebSocketReceiveResult result = await this._socket.ReceiveAsync(buffer, ct);
        if (result.MessageType == WebSocketMessageType.Close)
            return new IrcSocketFrame(0, Closed: true);

        if (result.MessageType == WebSocketMessageType.Binary)
        {
            while (!result.EndOfMessage)
            {
                result = await this._socket.ReceiveAsync(buffer, ct);
            }

            return new IrcSocketFrame(0, Closed: false);
        }

        return new IrcSocketFrame(result.Count, Closed: false);
    }

    public async Task SendTextAsync(string text, CancellationToken ct)
    {
        await this._socket.SendAsync(
            Encoding.UTF8.GetBytes(text),
            WebSocketMessageType.Text,
            endOfMessage: true,
            ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (this._socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var closeTimeout = new CancellationTokenSource(CloseTimeout);
            try
            {
                await this._socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "shutdown", closeTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                this._socket.Abort();
            }
            catch (WebSocketException)
            {
                this._socket.Abort();
            }
        }

        this._socket.Dispose();
    }
}
