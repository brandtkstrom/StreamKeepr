namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// One part playlist as read from <c>pNNN.m3u8</c>. Parts are identified by their numeric part
/// number (1-999) so the stitcher can order them independently of caller enumeration order.
/// </summary>
public sealed record PartPlaylist(int PartNumber, string Content);
