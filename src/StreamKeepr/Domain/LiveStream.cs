namespace StreamKeepr.Domain;

/// <summary>
/// A live broadcast as observed from Twitch. The stream ID, not the title or start time,
/// identifies the stream. <see cref="ViewerCount"/> is the Helix <c>viewer_count</c> when the
/// observation came from <c>/streams</c>; EventSub <c>stream.online</c> carries none, so it is null.
/// It is sampled for the viewer chart and is not stream metadata.
/// </summary>
public sealed record LiveStream(
    string StreamId,
    string Title,
    string Game,
    DateTimeOffset StartedAt,
    string? ThumbnailUrl,
    int? ViewerCount = null);
