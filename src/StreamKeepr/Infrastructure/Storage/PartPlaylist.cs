using System.Collections.Immutable;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// One part playlist as read from <c>pNNN.m3u8</c>. Parts are identified by their numeric part
/// number (1-999) so the stitcher can order them independently of caller enumeration order.
/// </summary>
public sealed record PartPlaylist(int PartNumber, string Content);

/// <summary>
/// One part playlist plus the metadata and parsed body a live refresh compares before reading
/// the file again. Content is retained for the exact prefix test that accepts an append.
/// </summary>
internal sealed record PartSnapshot(
    int PartNumber,
    long Length,
    long LastWriteTicks,
    string Content,
    ImmutableArray<string> BodyLines,
    int TargetDuration)
{
    public bool Matches(long length, long lastWriteTicks) =>
        Length == length && LastWriteTicks == lastWriteTicks;

    public ParsedPart ToParsed() => new(PartNumber, BodyLines, TargetDuration);
}
