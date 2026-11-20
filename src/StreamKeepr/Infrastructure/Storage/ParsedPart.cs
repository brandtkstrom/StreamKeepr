using System.Collections.Immutable;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// One part playlist as seen by the shared stitcher parser: the body lines through the last
/// complete segment URI plus the part's declared target duration. Both the full stitch and the
/// incremental composer consume this shape so they cannot diverge.
/// </summary>
internal readonly record struct ParsedPart(
    int PartNumber,
    ImmutableArray<string> BodyLines,
    int TargetDuration);
