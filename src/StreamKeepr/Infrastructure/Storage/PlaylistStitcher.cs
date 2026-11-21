using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Pure HLS stitching for one session: parses part playlists and renders the live index and
/// the session index from them. Callers read the part playlists and persist the result; this
/// type performs no I/O and uses no clock. Reading a playlist's timeline is
/// <see cref="PlaylistTimeline"/>.
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
    private const string DiscontinuityLine = "#EXT-X-DISCONTINUITY\n";

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

        return Render(parsed, minTargetDuration, final);
    }

    /// <summary>
    /// Renders already parsed parts as the live index (<paramref name="final"/> false) or the
    /// session index (true). Callers must pass parts in ascending part order. This is the one
    /// place the header, the part order, and the discontinuity rule live.
    /// </summary>
    internal static string Render(
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
            if (!part.HasSegments)
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
    /// declared target.
    /// </summary>
    private static int EffectiveTargetDuration(
        IEnumerable<int> partTargets, int minTargetDuration)
    {
        int targetDuration = minTargetDuration;
        foreach (int partTarget in partTargets)
            targetDuration = Math.Max(targetDuration, partTarget);
        return targetDuration;
    }

    /// <summary>
    /// Parses one part playlist. A final line without a line feed is still being written and is
    /// never exposed; body lines stop at the last complete segment line, so a trailing
    /// <c>#EXTINF</c> is dropped as well.
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

    /// <summary>
    /// True when the content is a whole part playlist as FFmpeg renames it into place: it starts
    /// with <c>#EXTM3U</c> and ends with a line feed. Anything else is a partial write or a
    /// contract violation and must never replace a live index's last good body.
    /// </summary>
    internal static bool IsCompletePart(string content) =>
        content.EndsWith('\n')
        && content.StartsWith(ExtM3uTag, StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="index"/> is a usable session index: it has at least one segment
    /// and an <c>#EXT-X-ENDLIST</c> line. Throws <see cref="FormatException"/> when the playlist
    /// is malformed, so the caller can report it.
    /// </summary>
    internal static bool IsFinalIndex(string index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return PlaylistTimeline.Parse(index).SegmentCount > 0
            && index.Split('\n').Any(line => line.Trim() == EndListTag);
    }

    /// <summary>
    /// Every media file a part playlist names: <c>#EXT-X-MAP</c> init files (read by
    /// <see cref="PlaylistTimeline.TryReadMapUri"/>, the same rule thumbnails use) and segment URIs.
    /// Deliberately looser than <see cref="ParsePart"/>: an unterminated last line still counts,
    /// because recovery deletes media that no playlist references, and a segment whose line was
    /// still being written must survive.
    /// </summary>
    internal static IReadOnlySet<string> ReferencedMedia(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (PlaylistTimeline.TryReadMapUri(line, out string init))
            {
                referenced.Add(init);
            }
            else if (line[0] != '#')
            {
                referenced.Add(line);
            }
        }

        return referenced;
    }

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

    private static bool IsPartHeader(string line) =>
        PartHeaderTags.Any(tag => line.StartsWith(tag, StringComparison.Ordinal));
}

