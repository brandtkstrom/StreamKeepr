namespace StreamKeepr.Infrastructure.Storage;

public enum LivePlaylistKind
{
    Live,
    Complete,
    Missing,
}

/// <summary>
/// One read from the session folder. <see cref="LivePlaylistKind.Complete"/> carries the
/// finalized index for callers that need any playable body (thumbnails); the background
/// refresher caches only <see cref="LivePlaylistKind.Live"/>.
/// </summary>
public sealed record LivePlaylistRead(LivePlaylistKind Kind, string? Body = null)
{
    public static LivePlaylistRead Missing { get; } = new(LivePlaylistKind.Missing);

    public static LivePlaylistRead Live(string body) => new(LivePlaylistKind.Live, body);

    public static LivePlaylistRead Complete(string? body) => new(LivePlaylistKind.Complete, body);
}

/// <summary>
/// The filesystem seam behind the live playlist refresher. Production uses
/// <see cref="SessionStoreLivePlaylistIo"/>; tests block or fail a read deterministically.
/// </summary>
public interface ILivePlaylistIo
{
    Task<LivePlaylistRead> ReadAsync(RecordingSummary summary, CancellationToken ct);
}
