using System.Collections.Immutable;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// One part playlist as parsed by <see cref="PlaylistStitcher.ParsePart"/>: the body lines
/// through the last complete segment line plus the part's declared target duration.
/// </summary>
internal readonly record struct ParsedPart(
    int PartNumber,
    ImmutableArray<string> BodyLines,
    int TargetDuration)
{
    /// <summary>
    /// The one definition of a part playlist that has segments: at least one complete segment
    /// line. A part without one adds no body and no discontinuity, and is not media.
    /// </summary>
    public bool HasSegments => !BodyLines.IsDefaultOrEmpty;
}
