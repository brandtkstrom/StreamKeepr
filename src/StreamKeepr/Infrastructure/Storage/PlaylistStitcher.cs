using System.Globalization;
using System.Text;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Pure HLS playlist stitching and timeline helpers for one session. Callers read the part
/// playlists and persist the returned playlist; this type performs no I/O and uses no clock.
/// </summary>
public static class PlaylistStitcher
{
    private const string ExtM3uTag = "#EXTM3U";
    private const string VersionTag = "#EXT-X-VERSION";
    private const string TargetDurationTag = "#EXT-X-TARGETDURATION";
    private const string MediaSequenceTag = "#EXT-X-MEDIA-SEQUENCE";
    private const string PlaylistTypeTag = "#EXT-X-PLAYLIST-TYPE";
    private const string IndependentSegmentsTag = "#EXT-X-INDEPENDENT-SEGMENTS";
    private const string EndListTag = "#EXT-X-ENDLIST";
    private const string DiscontinuityTag = "#EXT-X-DISCONTINUITY";
    private const string ProgramDateTimeTag = "#EXT-X-PROGRAM-DATE-TIME:";
    private const string ExtInfTag = "#EXTINF:";

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

        int targetDuration = minTargetDuration;
        var bodies = new List<List<string>>(parts.Count);
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

            targetDuration = Math.Max(targetDuration, TargetDurationOf(part.Content));

            var body = BodyLines(part.Content);
            if (body.Count > 0)
                bodies.Add(body);
        }

        var playlist = new StringBuilder()
            .Append(ExtM3uTag).Append('\n')
            .Append(VersionTag).Append(":7\n")
            .Append(TargetDurationTag).Append(':')
            .Append(targetDuration.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(MediaSequenceTag).Append(":0\n")
            .Append(PlaylistTypeTag).Append(":EVENT\n")
            .Append(IndependentSegmentsTag).Append('\n');

        for (int i = 0; i < bodies.Count; i++)
        {
            if (i > 0)
                playlist.Append(DiscontinuityTag).Append('\n');

            foreach (var line in bodies[i])
                playlist.Append(line).Append('\n');
        }

        if (final)
            playlist.Append(EndListTag).Append('\n');

        return playlist.ToString();
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

    // Keeps every line through the last segment URI, so a half-written trailing #EXTINF is dropped.
    private static List<string> BodyLines(string playlist)
    {
        var lines = new List<string>();
        int lastSegmentIndex = 0;

        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || IsPartHeader(line))
                continue;

            lines.Add(line);
            if (line[0] != '#')
                lastSegmentIndex = lines.Count;
        }

        return lines.GetRange(0, lastSegmentIndex);
    }

    private static int TargetDurationOf(string playlist)
    {
        int targetDuration = 0;

        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(TargetDurationTag, StringComparison.Ordinal))
                continue;

            var value = line.AsSpan(TargetDurationTag.Length);
            if (value.Length > 0 && value[0] == ':')
                value = value[1..];

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                && parsed > targetDuration)
            {
                targetDuration = parsed;
            }
        }

        return targetDuration;
    }

    private static bool TryParseSegments(string playlist, out List<Segment> segments)
    {
        segments = [];
        double? duration = null;
        DateTimeOffset? start = null;

        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith(ProgramDateTimeTag, StringComparison.Ordinal))
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

                segments.Add(new Segment(segmentDuration, start));
                duration = null;
            }
        }

        return true;
    }

    private static bool IsPartHeader(string line) =>
        PartHeaderTags.Any(tag => line.StartsWith(tag, StringComparison.Ordinal));

    private readonly record struct Segment(double Duration, DateTimeOffset? Start);
}
