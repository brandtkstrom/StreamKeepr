using System.Text.RegularExpressions;

namespace StreamKeepr.Domain;

/// <summary>
/// One continuous recording of a stream. <see cref="Id"/> combines the UTC start time and
/// stream ID and is a safe path segment; titles never enter IDs or directories.
/// </summary>
public sealed record Session(
    string Id, string Channel, string StreamId, DateTimeOffset StartedAt, string Directory)
{
    private static readonly Regex IdPattern = new(
        @"^\d{8}T\d{6}Z_\d+$", RegexOptions.CultureInvariant);

    /// <summary>True for the <see cref="For"/> id shape; used to reject unsafe public request ids.</summary>
    public static bool IsValidId(string id) => IdPattern.IsMatch(id);

    /// <summary>
    /// Creates the session for a live stream. <paramref name="channel"/> is a validated Twitch
    /// login and <see cref="LiveStream.StreamId"/> is digits, so the derived ID is path-safe.
    /// </summary>
    public static Session For(LiveStream live, string channel, string recordingsRoot)
    {
        string id = $"{live.StartedAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}_{live.StreamId}";
        if (!IsValidId(id))
        {
            throw new ArgumentException(
                "The stream id cannot form a safe session id.", nameof(live));
        }

        return new(id, channel, live.StreamId, live.StartedAt, Path.Combine(recordingsRoot, channel, id));
    }
}
