namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>Twitch EventSub push client. Routes one observation per notification.</summary>
public interface IEventSubClient
{
    Task RunAsync(StreamObservationRoute route, CancellationToken ct);
}

/// <summary>Creates one WebSocket per EventSub session; tests supply a fake.</summary>
public interface IEventSubSocketFactory
{
    IEventSubSocket Create();
}

public interface IEventSubSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);

    /// <summary>Returns null when Twitch closes the socket.</summary>
    Task<string?> ReceiveTextAsync(CancellationToken ct);
}
