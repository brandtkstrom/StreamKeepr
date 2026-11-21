using System.Collections.Immutable;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// What a live index build produced: the ordered parsed parts and the body rendered from them.
/// The next build starts from it so unchanged parts are never read again.
/// </summary>
internal sealed record LiveComposition(ImmutableArray<PartSnapshot> Parts, string Body)
{
    public static LiveComposition Empty(int minTargetDuration) =>
        new(
            ImmutableArray<PartSnapshot>.Empty,
            PlaylistStitcher.Render([], minTargetDuration, final: false));
}
