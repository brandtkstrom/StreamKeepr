namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// The derived library projection of one session. Nothing here is stored in the manifest;
/// counts, duration, size, thumbnail presence, and chapters come from the session folder.
/// </summary>
public sealed record RecordingSummary(
    string Id,
    string Channel,
    string Title,
    string Game,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    bool IsRecording,
    double? DurationSeconds,
    long? SizeBytes,
    int PartCount,
    int SegmentCount,
    bool HasThumbnail,
    IReadOnlyList<Chapter> Chapters,
    bool HasChat = false)
{
    public string PlaylistUrl => $"/media/{Channel}/{Id}/index.m3u8";

    public string? ThumbnailUrl => HasThumbnail ? $"/media/{Channel}/{Id}/thumb.jpg" : null;
}
