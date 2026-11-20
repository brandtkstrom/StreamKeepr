using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Pure HLS playlist stitching and timeline helpers for one session. Callers read the part
/// playlists and persist the returned playlist; this type performs no I/O and uses no clock.
/// </summary>
public static class PlaylistStitcher
{
    internal const string ExtM3uTag = "#EXTM3U";
    private const string VersionTag = "#EXT-X-VERSION";
    private const string TargetDurationTag = "#EXT-X-TARGETDURATION";
    private const string MediaSequenceTag = "#EXT-X-MEDIA-SEQUENCE";
    private const string PlaylistTypeTag = "#EXT-X-PLAYLIST-TYPE";
    private const string IndependentSegmentsTag = "#EXT-X-INDEPENDENT-SEGMENTS";
    private const string EndListTag = "#EXT-X-ENDLIST";
    private const string ProgramDateTimeTag = "#EXT-X-PROGRAM-DATE-TIME:";
    private const string ExtInfTag = "#EXTINF:";
    private const string MapUriPrefix = "#EXT-X-MAP:URI=\"";
    internal const string DiscontinuityLine = "#EXT-X-DISCONTINUITY\n";

    private static readonly string[] PartHeaderTags =
    [
        ExtM3uTag,
        VersionTag,
        TargetDurationTag,
        MediaSequenceTag,
        PlaylistTypeTag,
        IndependentSegmentsTag,
        EndListTag,
    ];

    /// <summary>
    /// Stitches part playlists into one session playlist in ascending numeric part order.
    /// </summary>
    /// <param name="parts">The part playlists; enumeration order is irrelevant.</param>
    /// <param name="minTargetDuration">
    /// The caller's floor for <c>#EXT-X-TARGETDURATION</c>. The session store passes
    /// <c>2 * Recorder:SegmentSeconds</c>.
    /// </param>
    /// <param name="final">
    /// When true, append <c>#EXT-X-ENDLIST</c>; while recording, leave the playlist open.
    /// </param>
    public static string Stitch(IReadOnlyList<PartPlaylist> parts, int minTargetDuration, bool final)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentOutOfRangeException.ThrowIfLessThan(minTargetDuration, 1);

        var parsed = new List<ParsedPart>(parts.Count);
        var partNumbers = new HashSet<int>();

        foreach (var part in parts.OrderBy(part => part.PartNumber))
        {
            ArgumentNullException.ThrowIfNull(part);
            ArgumentOutOfRangeException.ThrowIfLessThan(part.PartNumber, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(part.PartNumber, 999);
            if (!partNumbers.Add(part.PartNumber))
                throw new ArgumentException($"Duplicate part number {part.PartNumber}.", nameof(parts));
            if (part.Content is null)
                throw new ArgumentException($"Part {part.PartNumber} has no playlist content.", nameof(parts));

            parsed.Add(ParsePart(part.PartNumber, part.Content));
        }

        return StitchParsed(parsed, minTargetDuration, final);
    }

    /// <summary>
    /// Stitches already parsed parts. Callers must pass parts in ascending part order.
    /// </summary>
    internal static string StitchParsed(
        IReadOnlyList<ParsedPart> parts, int minTargetDuration, bool final)
    {
        int targetDuration = EffectiveTargetDuration(
            parts.Select(part => part.TargetDuration), minTargetDuration);

        var playlist = new StringBuilder()
            .Append(ExtM3uTag).Append('\n')
            .Append(VersionTag).Append(":7\n")
            .Append(TargetDurationTag).Append(':')
            .Append(targetDuration.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(MediaSequenceTag).Append(":0\n")
            .Append(PlaylistTypeTag).Append(":EVENT\n")
            .Append(IndependentSegmentsTag).Append('\n');

        bool hasBody = false;
        foreach (ParsedPart part in parts)
        {
            if (part.BodyLines.IsDefaultOrEmpty)
                continue;

            if (hasBody)
                playlist.Append(DiscontinuityLine);

            foreach (string line in part.BodyLines)
                playlist.Append(line).Append('\n');

            hasBody = true;
        }

        if (final)
            playlist.Append(EndListTag).Append('\n');

        return playlist.ToString();
    }

    /// <summary>
    /// The effective <c>#EXT-X-TARGETDURATION</c>: the caller's floor raised by any part's
    /// declared target. Shared so the full and incremental paths cannot drift.
    /// </summary>
    internal static int EffectiveTargetDuration(
        IEnumerable<int> partTargets, int minTargetDuration)
    {
        int targetDuration = minTargetDuration;
        foreach (int partTarget in partTargets)
            targetDuration = Math.Max(targetDuration, partTarget);
        return targetDuration;
    }

    /// <summary>
    /// Parses one part playlist through the same rules <see cref="Stitch"/> uses. A final line
    /// without a line feed is still being written and is never exposed; body lines stop at the
    /// last complete segment URI, so a trailing <c>#EXTINF</c> is dropped as well.
    /// </summary>
    internal static ParsedPart ParsePart(int partNumber, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var lines = new List<string>();
        int targetDuration = 0;
        int lastSegmentIndex = 0;

        string[] rawLines = content.Split('\n');
        int completeLines = content.EndsWith('\n') ? rawLines.Length : rawLines.Length - 1;

        for (int i = 0; i < completeLines; i++)
        {
            string line = rawLines[i].Trim();
            if (line.Length == 0)
                continue;

            if (TryParseTargetDuration(line, out int partTarget))
                targetDuration = Math.Max(targetDuration, partTarget);

            if (IsPartHeader(line))
                continue;

            lines.Add(line);
            if (line[0] != '#')
                lastSegmentIndex = lines.Count;
        }

        return new ParsedPart(
            partNumber,
            lines.GetRange(0, lastSegmentIndex).ToImmutableArray(),
            targetDuration);
    }

    /// <summary>Sum of every <c>#EXTINF</c> duration in the playlist, in seconds.</summary>
    public static double TotalDuration(string playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        if (!TryParseSegments(playlist, out var segments))
            throw new FormatException("The playlist contains a malformed segment entry.");

        double total = 0;
        foreach (var segment in segments)
            total += segment.Duration;
        return total;
    }

    /// <summary>Number of segment URI lines in the playlist; tag and init-map lines do not count.</summary>
    public static int SegmentCount(string playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        if (!TryParseSegments(playlist, out var segments))
            throw new FormatException("The playlist contains a malformed segment entry.");

        return segments.Count;
    }

    /// <summary>The first <c>#EXT-X-PROGRAM-DATE-TIME</c> in a playlist, if present.</summary>
    public static DateTimeOffset? FirstProgramDateTime(string playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        foreach (string raw in playlist.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith(ProgramDateTimeTag, StringComparison.Ordinal))
                continue;

            return DateTimeOffset.TryParse(
                line.AsSpan(ProgramDateTimeTag.Length),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset parsed)
                ? parsed
                : null;
        }

        return null;
    }

    /// <summary>
    /// Positions title/game changes on the stitched timeline. The first metadata entry describes
    /// the session start and is not a chapter; every later change begins at the first segment whose
    /// program-date-time window contains it. A null or unparseable playlist yields no chapters.
    /// </summary>
    public static IReadOnlyList<Chapter> Chapters(string? playlist, IReadOnlyList<MetadataChange> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (playlist is null || metadata.Count <= 1)
            return [];
        if (!TryParseSegments(playlist, out var segments))
            return [];

        var chapters = new List<Chapter>();
        int nextChange = 1;
        double offset = 0;

        foreach (var segment in segments)
        {
            if (segment.Start is { } segmentStart)
            {
                var segmentEnd = segmentStart.AddSeconds(segment.Duration);
                while (nextChange < metadata.Count && metadata[nextChange].At <= segmentEnd)
                {
                    var change = metadata[nextChange];
                    chapters.Add(new Chapter(offset, change.Title, change.Game));
                    nextChange++;
                }
            }

            offset += segment.Duration;
        }

        return chapters;
    }

    /// <summary>
    /// The init segment and media segment that cover <paramref name="offsetSeconds"/> on the
    /// playlist timeline. Returns null when the offset is negative or past the last segment, the
    /// playlist is malformed, the segment has no <c>#EXT-X-MAP</c>, or either name is not a plain
    /// file name next to the playlist (a playlist never points a reader elsewhere).
    /// </summary>
    public static SegmentFiles? SegmentAt(string playlist, double offsetSeconds)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        if (!double.IsFinite(offsetSeconds) || offsetSeconds < 0)
            return null;
        if (!TryParseSegments(playlist, out var segments))
            return null;

        double end = 0;
        foreach (var segment in segments)
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

    private static bool TryParseTargetDuration(string line, out int targetDuration)
    {
        targetDuration = 0;
        if (!line.StartsWith(TargetDurationTag, StringComparison.Ordinal))
            return false;

        var value = line.AsSpan(TargetDurationTag.Length);
        if (value.Length > 0 && value[0] == ':')
            value = value[1..];

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out targetDuration)
            && targetDuration > 0;
    }

    private static bool TryParseSegments(string playlist, out List<Segment> segments)
    {
        segments = [];
        double? duration = null;
        DateTimeOffset? start = null;
        string? map = null;

        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
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
                        out var parsed))
                {
                    return false;
                }

                start = parsed;
            }
            else if (line.StartsWith(ExtInfTag, StringComparison.Ordinal))
            {
                var value = line.AsSpan(ExtInfTag.Length);
                int comma = value.IndexOf(',');
                if (comma >= 0)
                    value = value[..comma];

                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                    || parsed < 0)
                {
                    return false;
                }

                duration = parsed;
            }
            else if (line[0] != '#')
            {
                if (duration is not { } segmentDuration)
                    return false;

                segments.Add(new Segment(segmentDuration, start, line, map));
                duration = null;
            }
        }

        return true;
    }

    private static bool IsPartHeader(string line) =>
        PartHeaderTags.Any(tag => line.StartsWith(tag, StringComparison.Ordinal));

    private readonly record struct Segment(double Duration, DateTimeOffset? Start, string Uri, string? Map);
}

/// <summary>An init segment and one media segment, as plain file names in the session folder.</summary>
public sealed record SegmentFiles(string Init, string Media);
