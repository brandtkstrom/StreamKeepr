using System.Collections.Immutable;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// One immutable per-session live playlist: the ordered parts it was built from and the
/// stitched body. A refresh replaces the whole record; readers never observe a partial body.
/// </summary>
internal sealed record LiveSnapshot(
    ImmutableArray<PartSnapshot> Parts,
    string Body,
    int TargetDuration)
{
    public static LiveSnapshot Empty(int minTargetDuration) =>
        new(
            ImmutableArray<PartSnapshot>.Empty,
            PlaylistStitcher.StitchParsed([], minTargetDuration, final: false),
            minTargetDuration);
}
