using System.Collections.Immutable;
using System.Globalization;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// The media timeline of one HLS playlist (a part playlist, the live index, or the session
/// index), parsed once. Every question about duration, segments, program-date-time, chapters,
/// or the segment at an offset is answered from the same parse.
/// </summary>
/// <remarks>
/// A segment is a URI line preceded by an <c>#EXTINF</c>; a trailing <c>#EXTINF</c> without a
/// URI is not a segment. A URI without an <c>#EXTINF</c>, a negative or unreadable duration, or an
/// unreadable <c>#EXT-X-PROGRAM-DATE-TIME</c> makes the whole playlist malformed. Callers choose
/// how a malformed playlist behaves: <see cref="Parse"/> throws, <see cref="TryParse"/> returns null.
/// </remarks>
public sealed class PlaylistTimeline
{
    private const string ProgramDateTimeTag = "#EXT-X-PROGRAM-DATE-TIME:";
    private const string ExtInfTag = "#EXTINF:";
    private const string MapUriPrefix = "#EXT-X-MAP:URI=\"";

    private readonly ImmutableArray<Segment> _segments;

    private PlaylistTimeline(ImmutableArray<Segment> segments, DateTimeOffset? firstProgramDateTime)
    {
        _segments = segments;
        FirstProgramDateTime = firstProgramDateTime;

        double duration = 0;
        foreach (Segment segment in segments)
            duration += segment.Duration;
        Duration = duration;
    }

    /// <summary>Sum of every segment duration, in seconds.</summary>
    public double Duration { get; }

    /// <summary>Number of segment URI lines; tag and init-map lines do not count.</summary>
    public int SegmentCount => _segments.Length;

    /// <summary>The first <c>#EXT-X-PROGRAM-DATE-TIME</c>, read as UTC when it has no offset.</summary>
    public DateTimeOffset? FirstProgramDateTime { get; }

    /// <summary>Parses a playlist, or throws <see cref="FormatException"/> when it is malformed.</summary>
    public static PlaylistTimeline Parse(string playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        return TryParse(playlist)
            ?? throw new FormatException("The playlist contains a malformed segment entry.");
    }

    /// <summary>Parses a playlist, or returns null when it is missing or malformed.</summary>
    public static PlaylistTimeline? TryParse(string? playlist)
    {
        if (playlist is null)
            return null;

        var segments = ImmutableArray.CreateBuilder<Segment>();
        double? duration = null;
        DateTimeOffset? start = null;
        DateTimeOffset? first = null;
        string? map = null;

        foreach (string raw in playlist.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith(MapUriPrefix, StringComparison.Ordinal))
            {
                int close = line.IndexOf('"', MapUriPrefix.Length);
                map = close > MapUriPrefix.Length ? line[MapUriPrefix.Length..close] : null;
            }
            else if (line.StartsWith(ProgramDateTimeTag, StringComparison.Ordinal))
            {
                if (!DateTimeOffset.TryParse(
                        line.AsSpan(ProgramDateTimeTag.Length),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal,
                        out DateTimeOffset parsed))
                {
                    return null;
                }

                start = parsed;
                first ??= parsed;
            }
            else if (line.StartsWith(ExtInfTag, StringComparison.Ordinal))
            {
                ReadOnlySpan<char> value = line.AsSpan(ExtInfTag.Length);
                int comma = value.IndexOf(',');
                if (comma >= 0)
                    value = value[..comma];

                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                    || parsed < 0)
                {
                    return null;
                }

                duration = parsed;
            }
            else if (line[0] != '#')
            {
                if (duration is not { } segmentDuration)
                    return null;

                segments.Add(new Segment(segmentDuration, start, line, map));
                duration = null;
            }
        }

        return new PlaylistTimeline(segments.ToImmutable(), first);
    }

    /// <summary>
    /// Positions title/game changes on the timeline. The first metadata entry describes the
    /// session start and is not a chapter; every later change begins at the first segment whose
    /// program-date-time window contains it.
    /// </summary>
    public IReadOnlyList<Chapter> Chapters(IReadOnlyList<MetadataChange> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Count <= 1)
            return [];

        var chapters = new List<Chapter>();
        int nextChange = 1;
        double offset = 0;

        foreach (Segment segment in _segments)
        {
            if (segment.Start is { } segmentStart)
            {
                DateTimeOffset segmentEnd = segmentStart.AddSeconds(segment.Duration);
                while (nextChange < metadata.Count && metadata[nextChange].At <= segmentEnd)
                {
                    MetadataChange change = metadata[nextChange];
                    chapters.Add(new Chapter(offset, change.Title, change.Game));
                    nextChange++;
                }
            }

            offset += segment.Duration;
        }

        return chapters;
    }

    /// <summary>
    /// The init segment and media segment that cover <paramref name="offsetSeconds"/>. Returns
    /// null when the offset is negative or past the last segment, the segment has no
    /// <c>#EXT-X-MAP</c>, or either name is not a plain file name next to the playlist (a
    /// playlist never points a reader elsewhere).
    /// </summary>
    public SegmentFiles? SegmentAt(double offsetSeconds)
    {
        if (!double.IsFinite(offsetSeconds) || offsetSeconds < 0)
            return null;

        double end = 0;
        foreach (Segment segment in _segments)
        {
            end += segment.Duration;
            if (offsetSeconds >= end)
                continue;

            return segment.Map is { } map && IsPlainFileName(map) && IsPlainFileName(segment.Uri)
                ? new SegmentFiles(map, segment.Uri)
                : null;
        }

        return null;
    }

    private static bool IsPlainFileName(string name) =>
        name.Length > 0 && Path.GetFileName(name) == name && name is not ("." or "..");

    private readonly record struct Segment(double Duration, DateTimeOffset? Start, string Uri, string? Map);
}

/// <summary>An init segment and one media segment, as plain file names in the session folder.</summary>
public sealed record SegmentFiles(string Init, string Media);
