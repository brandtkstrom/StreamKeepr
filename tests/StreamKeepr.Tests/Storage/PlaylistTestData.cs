using System.Globalization;

namespace StreamKeepr.Tests.Storage;

internal static class PlaylistTestData
{
    public static string Lines(params string[] lines) => string.Join('\n', lines);

    /// <summary>A finalized part playlist; FFmpeg always terminates the last line.</summary>
    public static string Playlist(int targetDuration, string init, params string[][] segments) =>
        Build(targetDuration, init, final: true, segments);

    /// <summary>A recording part playlist: no ENDLIST, still newline-terminated.</summary>
    public static string LivePlaylist(int targetDuration, string init, params string[][] segments) =>
        Build(targetDuration, init, final: false, segments);

    private static string Build(int targetDuration, string init, bool final, string[][] segments)
    {
        var lines = new List<string>
        {
            "#EXTM3U",
            "#EXT-X-VERSION:7",
            $"#EXT-X-TARGETDURATION:{targetDuration.ToString(CultureInfo.InvariantCulture)}",
            "#EXT-X-MEDIA-SEQUENCE:0",
            "#EXT-X-PLAYLIST-TYPE:EVENT",
            "#EXT-X-INDEPENDENT-SEGMENTS",
            $"#EXT-X-MAP:URI=\"{init}\"",
        };

        foreach (var segment in segments)
            lines.AddRange(segment);

        if (final)
            lines.Add("#EXT-X-ENDLIST");

        return string.Join('\n', lines) + "\n";
    }

    public static string[] Segment(string uri, string programDateTime, double duration) =>
    [
        $"#EXT-X-PROGRAM-DATE-TIME:{programDateTime}",
        $"#EXTINF:{duration.ToString(CultureInfo.InvariantCulture)},",
        uri,
    ];
}
