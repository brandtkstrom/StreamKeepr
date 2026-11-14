using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Infrastructure.Twitch.Chat;

/// <summary>The capture service's view of a chat transport. The IRC and Mock sources share it.</summary>
public interface IChatSource : IAsyncDisposable
{
    string Source { get; }

    ValueTask JoinAsync(string channel, CancellationToken ct);

    ValueTask PartAsync(string channel, CancellationToken ct);

    IAsyncEnumerable<ChatSourceEvent> ReadAllAsync(CancellationToken ct);
}
