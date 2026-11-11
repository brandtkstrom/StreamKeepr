namespace StreamKeepr.Domain;

/// <summary>
/// A live broadcast as observed from Twitch. The stream ID, not the title or start time,
/// identifies the stream.
/// </summary>
public sealed record LiveStream(
    string StreamId, string Title, string Game, DateTimeOffset StartedAt, string? ThumbnailUrl);
