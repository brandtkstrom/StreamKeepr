using System.Collections.Immutable;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// One immutable per-session live index: the ordered parts it was rendered from and the
/// rendered body. A refresh replaces the whole record; readers never observe a partial body.
/// </summary>
internal sealed record LiveSnapshot(ImmutableArray<PartSnapshot> Parts, string Body)
{
    public static LiveSnapshot Empty(int minTargetDuration) =>
        new(
            ImmutableArray<PartSnapshot>.Empty,
            PlaylistStitcher.Render([], minTargetDuration, final: false));
}
