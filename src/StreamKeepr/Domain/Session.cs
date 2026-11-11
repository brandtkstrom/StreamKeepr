namespace StreamKeepr.Domain;

/// <summary>
/// One continuous recording of a stream. <see cref="Id"/> combines the UTC start time and
/// stream ID and is a safe path segment; titles never enter IDs or directories.
/// </summary>
public sealed record Session(
    string Id, string Channel, string StreamId, DateTimeOffset StartedAt, string Directory)
{
    /// <summary>
    /// Creates the session for a live stream. <paramref name="channel"/> is a validated Twitch
    /// login and <see cref="LiveStream.StreamId"/> is digits, so the derived ID is path-safe.
    /// </summary>
    public static Session For(LiveStream live, string channel, string recordingsRoot)
    {
        string id = $"{live.StartedAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}_{live.StreamId}";
        return new(id, channel, live.StreamId, live.StartedAt, Path.Combine(recordingsRoot, channel, id));
    }
}
