using StreamKeepr.Infrastructure.Media;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Maps between Twitch wall-clock timestamps and the stitched media timeline. Each part has its
/// own FFmpeg muxer-start PDT; the content at the part start aired roughly <c>leadIn</c> earlier.
/// </summary>
public sealed class ChatTimeline
{
    /// <summary>PDT and segment rounding make adjacent parts differ by well under a second.</summary>
    private const double MinPartGapSeconds = 1;

    private readonly IReadOnlyList<PartTiming> _parts;

    private ChatTimeline(IReadOnlyList<PartTiming> parts, double leadInSeconds)
    {
        _parts = parts;
        LeadInSeconds = leadInSeconds;
    }

    public double LeadInSeconds { get; }

    public bool HasParts => _parts.Count > 0;

    /// <summary>
    /// Reads the part playlists. An unreadable part is logged and left out, so chat for it is
    /// unmapped rather than placed at a wrong time.
    /// </summary>
    public static ChatTimeline Build(string sessionDirectory, double leadInSeconds, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        if (!Directory.Exists(sessionDirectory))
            return new ChatTimeline([], leadInSeconds);

        var files = new List<(int Part, string Path)>();
        try
        {
            foreach (string path in Directory.EnumerateFiles(sessionDirectory, "p*.m3u8"))
            {
                if (PartFiles.TryParsePlaylist(Path.GetFileName(path), out int part) && part >= 1)
                    files.Add((part, path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not list parts in {Directory}; chat cannot be placed.",
                sessionDirectory);
            return new ChatTimeline([], leadInSeconds);
        }

        var parts = new List<PartTiming>();
        double mediaStart = 0;
        foreach ((int part, string path) in files.OrderBy(item => item.Part))
        {
            string? playlist = TryRead(path, logger);
            if (playlist is null)
                continue;

            double duration;
            try
            {
                duration = PlaylistStitcher.TotalDuration(playlist);
            }
            catch (FormatException ex)
            {
                logger.LogWarning(ex, "Part playlist {Path} is malformed; its chat is not placed.", path);
                continue;
            }

            DateTimeOffset? pdt = PlaylistStitcher.FirstProgramDateTime(playlist);
            if (pdt is { } start && duration > 0)
            {
                parts.Add(new PartTiming(
                    part,
                    start.AddSeconds(-leadInSeconds),
                    mediaStart,
                    duration));
            }

            mediaStart += duration;
        }

        return new ChatTimeline(parts, leadInSeconds);
    }

    /// <summary>Maps a server or local wall-clock timestamp to the stitched media timeline.</summary>
    public double? MapToMedia(long atMs)
    {
        DateTimeOffset at = DateTimeOffset.FromUnixTimeMilliseconds(atMs);
        PartTiming? match = null;
        foreach (PartTiming part in _parts)
        {
            DateTimeOffset end = part.ContentStart.AddSeconds(part.Duration);
            if (at < part.ContentStart || at > end)
                continue;

            if (match is null || part.MediaStart < match.MediaStart)
                match = part;
        }

        if (match is null)
            return null;

        double media = match.MediaStart +
                       (at - match.ContentStart).TotalSeconds;
        return Clamp(media, match.MediaStart, match.MediaStart + match.Duration);
    }

    /// <summary>Wall-clock ranges covering a media-time window, one per intersecting part.</summary>
    public IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> WallRanges(
        double fromSec, double toSec)
    {
        var ranges = new List<(DateTimeOffset, DateTimeOffset)>();
        foreach (PartTiming part in _parts)
        {
            double start = Math.Max(fromSec, part.MediaStart);
            double end = Math.Min(toSec, part.MediaStart + part.Duration);
            if (end <= start)
                continue;

            ranges.Add((
                part.ContentStart.AddSeconds(start - part.MediaStart),
                part.ContentStart.AddSeconds(end - part.MediaStart)));
        }

        return ranges;
    }

    /// <summary>
    /// Unrecorded wall-clock time between consecutive parts. The stitched media timeline is
    /// contiguous, so each gap sits at one media point: the later part's start.
    /// </summary>
    public IReadOnlyList<(double MediaAt, double Seconds)> PartGaps()
    {
        var gaps = new List<(double, double)>();
        for (int i = 1; i < _parts.Count; i++)
        {
            PartTiming previous = _parts[i - 1];
            PartTiming next = _parts[i];
            double seconds = (next.ContentStart -
                              previous.ContentStart.AddSeconds(previous.Duration)).TotalSeconds;
            if (seconds >= MinPartGapSeconds)
                gaps.Add((next.MediaStart, seconds));
        }

        return gaps;
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Min(Math.Max(value, min), max);

    private static string? TryRead(string path, ILogger logger)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Part playlist {Path} could not be read; its chat is not placed.", path);
            return null;
        }
    }

    private sealed record PartTiming(
        int Part, DateTimeOffset ContentStart, double MediaStart, double Duration);
}
