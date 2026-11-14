using System.Globalization;
using System.Text.RegularExpressions;

namespace StreamKeepr.Infrastructure.Media;

/// <summary>
/// Names and cleanup rules for the files of one recorded part. Part numbers are zero-padded
/// and titles never enter a path.
/// </summary>
public static class PartFiles
{
    private static readonly Regex PlaylistName = new(
        @"^p(?<part>\d{3})\.m3u8$", RegexOptions.CultureInvariant);

    public static string Playlist(int part) => $"p{part:D3}.m3u8";

    /// <summary>
    /// Parses a part playlist name such as <c>p001.m3u8</c>. Callers apply their own range
    /// rules; part 0 parses but is not a valid part.
    /// </summary>
    public static bool TryParsePlaylist(string fileName, out int part)
    {
        Match match = PlaylistName.Match(fileName);
        part = match.Success
            ? int.Parse(match.Groups["part"].Value, CultureInfo.InvariantCulture)
            : 0;
        return match.Success;
    }

    public static string Init(int part) => $"init_{part:D3}.mp4";

    public static string SegmentPattern(int part) => $"p{part:D3}_%05d.m4s";

    /// <summary>True when any file from the part exists, including a leftover temporary file.</summary>
    public static bool Exists(string directory, int part)
    {
        if (!Directory.Exists(directory))
            return false;

        return Directory.EnumerateFiles(directory, $"p{part:D3}*").Any()
            || Directory.EnumerateFiles(directory, $"init_{part:D3}*").Any();
    }

    /// <summary>
    /// Removes every file of a part that produced no media, including <c>.tmp</c> files from
    /// FFmpeg's atomic rename. Does nothing when the directory does not exist.
    /// </summary>
    public static void Delete(string directory, int part)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (string file in Directory.EnumerateFiles(directory, $"p{part:D3}*").ToArray())
            File.Delete(file);

        foreach (string file in Directory.EnumerateFiles(directory, $"init_{part:D3}*").ToArray())
            File.Delete(file);
    }

    /// <summary>True when at least one completed media segment exists for the part.</summary>
    public static bool HasSegments(string directory, int part) =>
        Directory.Exists(directory)
        && Directory.EnumerateFiles(directory, $"p{part:D3}_*.m4s").Any();

    /// <summary>
    /// Sum of the part's init and completed media segments. Playlists and temporary files are
    /// excluded, so this is the authoritative byte count for progress reporting.
    /// </summary>
    public static long TotalBytes(string directory, int part)
    {
        if (!Directory.Exists(directory))
            return 0;

        long total = 0;
        foreach (string file in Directory.EnumerateFiles(directory, $"p{part:D3}_*.m4s"))
            total += new FileInfo(file).Length;
        foreach (string file in Directory.EnumerateFiles(directory, $"init_{part:D3}*.mp4"))
            total += new FileInfo(file).Length;

        return total;
    }
}
