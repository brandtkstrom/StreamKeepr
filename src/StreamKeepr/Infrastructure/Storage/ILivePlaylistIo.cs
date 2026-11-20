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

    public static LivePlaylistRead Live(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new(LivePlaylistKind.Live, body);
    }

    public static LivePlaylistRead Complete(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new(LivePlaylistKind.Complete, body);
    }
}

/// <summary>
/// The filesystem seam behind the live playlist refresher. Production uses
/// <see cref="SessionStoreLivePlaylistIo"/>; tests block or fail a read deterministically.
/// </summary>
public interface ILivePlaylistIo
{
    Task<LivePlaylistRead> ReadAsync(RecordingSummary summary, CancellationToken ct);
}

/// <summary>
/// Optional lifecycle hook for an <see cref="ILivePlaylistIo"/> that keeps its own per-session
/// snapshot. The service calls it when a session leaves the live set so the IO can release the
/// parsed parts immediately instead of waiting for a read that may never come.
/// </summary>
internal interface ILivePlaylistSnapshotInvalidator
{
    void Remove(string id);
}
