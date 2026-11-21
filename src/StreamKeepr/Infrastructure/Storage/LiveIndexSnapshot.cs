using System.Text;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// One published live index for a Recording session. The body and its pre-encoded UTF-8 bytes
/// are published together so a request never re-encodes or re-reads the session folder.
/// </summary>
public sealed record LiveIndexSnapshot(
    string Id,
    long Version,
    string Body,
    ReadOnlyMemory<byte> Utf8,
    int SegmentCount,
    DateTimeOffset RefreshedAt)
{
    /// <summary>Builds the snapshot and its UTF-8 body in one step.</summary>
    public static LiveIndexSnapshot Create(
        string id, long version, string body, int segmentCount, DateTimeOffset refreshedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfNegative(segmentCount);

        return new(
            id,
            version,
            body,
            Encoding.UTF8.GetBytes(body),
            segmentCount,
            refreshedAt);
    }
}
