namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// One part playlist as read from <c>pNNN.m3u8</c>. Parts are identified by their numeric part
/// number (1-999) so the stitcher can order them independently of caller enumeration order.
/// </summary>
public sealed record PartPlaylist(int PartNumber, string Content);

/// <summary>
/// One parsed part playlist plus the file metadata a live refresh compares before reading the
/// file again.
/// </summary>
internal sealed record PartSnapshot(ParsedPart Parsed, long Length, long LastWriteTicks)
{
    public int PartNumber => Parsed.PartNumber;

    public bool Matches(long length, long lastWriteTicks) =>
        Length == length && LastWriteTicks == lastWriteTicks;
}
