using System.Globalization;
using System.Text.RegularExpressions;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Maps between Twitch wall-clock timestamps and the stitched media timeline. Each part has its
/// own FFmpeg muxer-start PDT; the content at the part start aired roughly <c>leadIn</c> earlier.
/// </summary>
public sealed partial class ChatTimeline
{
    /// <summary>PDT and segment rounding make adjacent parts differ by well under a second.</summary>
    private const double MinPartGapSeconds = 1;

    private readonly IReadOnlyList<PartTiming> _parts;

    private ChatTimeline(IReadOnlyList<PartTiming> parts, double leadInSeconds)
    {
        this._parts = parts;
        this.LeadInSeconds = leadInSeconds;
    }

    public double LeadInSeconds { get; }

    public bool HasParts => this._parts.Count > 0;

    public static ChatTimeline Build(string sessionDirectory, double leadInSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        if (!Directory.Exists(sessionDirectory))
            return new ChatTimeline([], leadInSeconds);

        var files = new List<(int Part, string Path)>();
        try
        {
            foreach (string path in Directory.EnumerateFiles(sessionDirectory, "p*.m3u8"))
            {
                Match match = PartFilePattern().Match(Path.GetFileName(path));
                if (match.Success &&
                    int.TryParse(match.Groups[1].Value, NumberStyles.None,
                                 CultureInfo.InvariantCulture, out int part))
                {
                    files.Add((part, path));
                }
            }
        }
        catch (IOException)
        {
            return new ChatTimeline([], leadInSeconds);
        }
        catch (UnauthorizedAccessException)
        {
            return new ChatTimeline([], leadInSeconds);
        }

        var parts = new List<PartTiming>();
        double mediaStart = 0;
        foreach ((int part, string path) in files.OrderBy(item => item.Part))
        {
            string? playlist = TryRead(path);
            if (playlist is null)
                continue;

            double duration;
            try
            {
                duration = PlaylistStitcher.TotalDuration(playlist);
            }
            catch (FormatException)
            {
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
        foreach (PartTiming part in this._parts)
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
        foreach (PartTiming part in this._parts)
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
        for (int i = 1; i < this._parts.Count; i++)
        {
            PartTiming previous = this._parts[i - 1];
            PartTiming next = this._parts[i];
            double seconds = (next.ContentStart -
                              previous.ContentStart.AddSeconds(previous.Duration)).TotalSeconds;
            if (seconds >= MinPartGapSeconds)
                gaps.Add((next.MediaStart, seconds));
        }

        return gaps;
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Min(Math.Max(value, min), max);

    private static string? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record PartTiming(
        int Part, DateTimeOffset ContentStart, double MediaStart, double Duration);

    [GeneratedRegex(@"^p(\d{3})\.m3u8$", RegexOptions.CultureInvariant)]
    private static partial Regex PartFilePattern();
}
