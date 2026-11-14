namespace StreamKeepr.Infrastructure.Twitch.Chat;

public readonly record struct IrcSocketFrame(int Count, bool Closed);

/// <summary>The tiny socket seam the IRC client needs. Tests supply scripted frames.</summary>
public interface IIrcSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);

    Task<IrcSocketFrame> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);

    Task SendTextAsync(string text, CancellationToken ct);
}

public interface IIrcSocketFactory
{
    IIrcSocket Create();
}
