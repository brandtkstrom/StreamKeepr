namespace StreamKeepr.Infrastructure.Twitch.Chat;

/// <summary>The capture service's view of a chat transport. The IRC and Mock sources share it.</summary>
public interface IChatSource : IAsyncDisposable
{
    ChatConnectionState State { get; }

    string Source { get; }

    DateTimeOffset? LastMessageAt { get; }

    event Action? Changed;

    ValueTask JoinAsync(string channel, CancellationToken ct);

    ValueTask PartAsync(string channel, CancellationToken ct);

    IAsyncEnumerable<ChatSourceEvent> ReadAllAsync(CancellationToken ct);
}
