using System.Text;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// One published live index for a Recording session. The body and its pre-encoded UTF-8 bytes
/// are published together so a request never re-encodes or re-reads the session folder.
/// <see cref="PublishedAt"/> is when this body was first published; an unchanged body keeps it.
/// </summary>
public sealed record LiveIndexSnapshot(
    string Id,
    string Body,
    ReadOnlyMemory<byte> Utf8,
    DateTimeOffset PublishedAt)
{
    /// <summary>Builds the snapshot and its UTF-8 body in one step.</summary>
    public static LiveIndexSnapshot Create(string id, string body, DateTimeOffset refreshedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(body);

        return new(id, body, Encoding.UTF8.GetBytes(body), refreshedAt);
    }
}
