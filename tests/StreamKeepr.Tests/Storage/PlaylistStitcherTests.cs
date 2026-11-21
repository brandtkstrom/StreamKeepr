using StreamKeepr.Infrastructure.Storage;
using static StreamKeepr.Tests.Storage.PlaylistTestData;

namespace StreamKeepr.Tests.Storage;

public sealed class PlaylistStitcherTests
{
    [Fact]
    public void Stitch_writes_the_event_header()
    {
        var part = new PartPlaylist(1, Playlist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6)));

        string[] lines = PlaylistStitcher.Stitch([part], 12, final: false).Split('\n');

        string[] header =
        [
            "#EXTM3U",
            "#EXT-X-VERSION:7",
            "#EXT-X-TARGETDURATION:12",
            "#EXT-X-MEDIA-SEQUENCE:0",
            "#EXT-X-PLAYLIST-TYPE:EVENT",
            "#EXT-X-INDEPENDENT-SEGMENTS",
        ];
        Assert.Equal(header, lines[..6]);
        Assert.Equal("#EXT-X-MAP:URI=\"init_001.mp4\"", lines[6]);
        Assert.Contains("#EXT-X-PROGRAM-DATE-TIME:2026-01-01T00:00:00+00:00", lines);
        Assert.Equal("p001_00001.m4s", lines[9]);
    }

    [Fact]
    public void Stitch_strips_part_header_tags_and_endlist()
    {
        var part = new PartPlaylist(1, Playlist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6)));

        string[] lines = PlaylistStitcher.Stitch([part], 12, final: true).Split('\n');

        Assert.Equal(1, lines.Count(line => line == "#EXTM3U"));
        Assert.Equal(1, lines.Count(line => line.StartsWith("#EXT-X-VERSION", StringComparison.Ordinal)));
        Assert.Equal(1, lines.Count(line => line.StartsWith("#EXT-X-TARGETDURATION", StringComparison.Ordinal)));
        Assert.Equal(1, lines.Count(line => line.StartsWith("#EXT-X-MEDIA-SEQUENCE", StringComparison.Ordinal)));
        Assert.Equal(1, lines.Count(line => line.StartsWith("#EXT-X-PLAYLIST-TYPE", StringComparison.Ordinal)));
        Assert.Equal(1, lines.Count(line => line == "#EXT-X-INDEPENDENT-SEGMENTS"));
        Assert.Equal(1, lines.Count(line => line == "#EXT-X-ENDLIST"));
        Assert.Equal("#EXT-X-ENDLIST", lines[^2]);
    }

    [Fact]
    public void Stitch_keeps_each_parts_init_map_and_program_date_time()
    {
        var first = new PartPlaylist(1, Playlist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6)));
        var second = new PartPlaylist(2, Playlist(6, "init_002.mp4",
            Segment("p002_00001.m4s", "2026-01-01T00:10:00+00:00", 6)));

        string[] lines = PlaylistStitcher.Stitch([first, second], 12, final: false).Split('\n');

        int firstMap = Array.IndexOf(lines, "#EXT-X-MAP:URI=\"init_001.mp4\"");
        int secondMap = Array.IndexOf(lines, "#EXT-X-MAP:URI=\"init_002.mp4\"");
        Assert.True(firstMap >= 0);
        Assert.True(secondMap > firstMap);
        Assert.Contains("#EXT-X-PROGRAM-DATE-TIME:2026-01-01T00:00:00+00:00", lines);
        Assert.Contains("#EXT-X-PROGRAM-DATE-TIME:2026-01-01T00:10:00+00:00", lines);
    }

    [Fact]
    public void Stitch_inserts_a_discontinuity_between_non_empty_parts()
    {
        var first = new PartPlaylist(1, Playlist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6)));
        var second = new PartPlaylist(2, Playlist(6, "init_002.mp4",
            Segment("p002_00001.m4s", "2026-01-01T00:00:06+00:00", 6)));

        string[] lines = PlaylistStitcher.Stitch([first, second], 12, final: false).Split('\n');

        int firstSegment = Array.IndexOf(lines, "p001_00001.m4s");
        int discontinuity = Array.IndexOf(lines, "#EXT-X-DISCONTINUITY");
        int secondMap = Array.IndexOf(lines, "#EXT-X-MAP:URI=\"init_002.mp4\"");
        Assert.True(firstSegment < discontinuity);
        Assert.True(discontinuity < secondMap);
        Assert.DoesNotContain("#EXT-X-DISCONTINUITY", lines[..firstSegment]);
    }

    [Fact]
    public void Stitch_skips_empty_parts_when_placing_discontinuities()
    {
        var first = new PartPlaylist(1, Playlist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6)));
        var headerOnly = new PartPlaylist(2, Playlist(6, "init_002.mp4"));
        var extInfOnly = new PartPlaylist(3, Lines(
            "#EXTM3U",
            "#EXT-X-TARGETDURATION:6",
            "#EXTINF:6,"));
        var last = new PartPlaylist(4, Playlist(6, "init_004.mp4",
            Segment("p004_00001.m4s", "2026-01-01T00:05:00+00:00", 6)));

        string[] lines = PlaylistStitcher
            .Stitch([first, headerOnly, extInfOnly, last], 12, final: false)
            .Split('\n');

        Assert.Equal(1, lines.Count(line => line == "#EXT-X-DISCONTINUITY"));
        Assert.Contains("p001_00001.m4s", lines);
        Assert.Contains("p004_00001.m4s", lines);
        Assert.DoesNotContain("#EXT-X-MAP:URI=\"init_002.mp4\"", lines);
        Assert.Equal(2, lines.Count(line => line.StartsWith("#EXTINF", StringComparison.Ordinal)));
    }

    [Fact]
    public void Stitch_drops_a_trailing_extinf_without_a_uri()
    {
        var part = new PartPlaylist(1, Lines(
            "#EXTM3U",
            "#EXT-X-VERSION:7",
            "#EXT-X-TARGETDURATION:6",
            "#EXT-X-MEDIA-SEQUENCE:0",
            "#EXT-X-PLAYLIST-TYPE:EVENT",
            "#EXT-X-INDEPENDENT-SEGMENTS",
            "#EXT-X-MAP:URI=\"init_001.mp4\"",
            "#EXTINF:6,",
            "p001_00001.m4s",
            "#EXTINF:6,"));

        string stitched = PlaylistStitcher.Stitch([part], 12, final: true);

        Assert.EndsWith("p001_00001.m4s\n#EXT-X-ENDLIST\n", stitched);
        Assert.Equal(1, stitched.Split('\n').Count(line => line.StartsWith("#EXTINF", StringComparison.Ordinal)));
    }

    [Fact]
    public void Stitch_appends_endlist_only_when_final()
    {
        var part = new PartPlaylist(1, Playlist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6)));

        string live = PlaylistStitcher.Stitch([part], 12, final: false);
        string final = PlaylistStitcher.Stitch([part], 12, final: true);

        Assert.DoesNotContain("#EXT-X-ENDLIST", live.Split('\n'));
        Assert.EndsWith("#EXT-X-ENDLIST\n", final);
        Assert.Equal(1, final.Split('\n').Count(line => line == "#EXT-X-ENDLIST"));
    }

    [Fact]
    public void Stitch_orders_parts_by_part_number()
    {
        var third = new PartPlaylist(3, Playlist(6, "init_003.mp4",
            Segment("p003_00001.m4s", "2026-01-01T00:00:18+00:00", 6)));
        var first = new PartPlaylist(1, Playlist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6)));
        var second = new PartPlaylist(2, Playlist(6, "init_002.mp4",
            Segment("p002_00001.m4s", "2026-01-01T00:00:09+00:00", 6)));

        string[] lines = PlaylistStitcher.Stitch([third, first, second], 12, final: false).Split('\n');

        int firstUri = Array.IndexOf(lines, "p001_00001.m4s");
        int secondUri = Array.IndexOf(lines, "p002_00001.m4s");
        int thirdUri = Array.IndexOf(lines, "p003_00001.m4s");
        Assert.True(firstUri < secondUri);
        Assert.True(secondUri < thirdUri);
        Assert.Equal(2, lines.Count(line => line == "#EXT-X-DISCONTINUITY"));
    }

    [Fact]
    public void Stitch_uses_line_feed_line_endings()
    {
        var part = new PartPlaylist(1, Playlist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6)).Replace("\n", "\r\n"));

        string stitched = PlaylistStitcher.Stitch([part], 12, final: true);

        Assert.DoesNotContain('\r', stitched);
        Assert.EndsWith("\n", stitched);
    }

    [Theory]
    [InlineData(6, 12, 12)]
    [InlineData(10, 12, 12)]
    [InlineData(20, 12, 20)]
    public void Stitch_target_duration_is_the_max_of_the_floor_and_part_maximum(
        int partTargetDuration,
        int minTargetDuration,
        int expected)
    {
        var part = new PartPlaylist(1, Playlist(partTargetDuration, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6)));

        string stitched = PlaylistStitcher.Stitch([part], minTargetDuration, final: false);

        Assert.Equal($"#EXT-X-TARGETDURATION:{expected}", stitched.Split('\n')[2]);
    }

    [Fact]
    public void Stitch_live_target_duration_may_drift_but_the_final_value_is_stable()
    {
        var first = new PartPlaylist(1, Playlist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6)));
        var second = new PartPlaylist(2, Playlist(6, "init_002.mp4",
            Segment("p002_00001.m4s", "2026-01-01T00:00:06+00:00", 6)));
        var longSegments = new PartPlaylist(3, Playlist(20, "init_003.mp4",
            Segment("p003_00001.m4s", "2026-01-01T00:00:12+00:00", 20)));

        string earlyLive = PlaylistStitcher.Stitch([first, second], 12, final: false);
        string laterLive = PlaylistStitcher.Stitch([first, second, longSegments], 12, final: false);
        string final = PlaylistStitcher.Stitch([first, second, longSegments], 12, final: true);

        Assert.Equal("#EXT-X-TARGETDURATION:12", earlyLive.Split('\n')[2]);
        Assert.Equal("#EXT-X-TARGETDURATION:20", laterLive.Split('\n')[2]);
        Assert.Equal("#EXT-X-TARGETDURATION:20", final.Split('\n')[2]);
    }

    [Fact]
    public void Stitch_with_no_parts_returns_a_valid_event_playlist()
    {
        string live = PlaylistStitcher.Stitch([], 12, final: false);
        string final = PlaylistStitcher.Stitch([], 12, final: true);

        Assert.Equal(
            Lines(
                "#EXTM3U",
                "#EXT-X-VERSION:7",
                "#EXT-X-TARGETDURATION:12",
                "#EXT-X-MEDIA-SEQUENCE:0",
                "#EXT-X-PLAYLIST-TYPE:EVENT",
                "#EXT-X-INDEPENDENT-SEGMENTS") + "\n",
            live);
        Assert.EndsWith("#EXT-X-ENDLIST\n", final);
        Assert.DoesNotContain("#EXT-X-DISCONTINUITY", final.Split('\n'));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void Stitch_rejects_part_numbers_outside_the_one_to_nine_hundred_ninety_nine_bound(int partNumber)
    {
        var part = new PartPlaylist(partNumber, Playlist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6)));

        Assert.Throws<ArgumentOutOfRangeException>(() => PlaylistStitcher.Stitch([part], 12, final: false));
    }

    [Fact]
    public void Stitch_rejects_duplicate_part_numbers()
    {
        var first = new PartPlaylist(1, Playlist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6)));
        var duplicate = new PartPlaylist(1, Playlist(6, "init_002.mp4",
            Segment("p002_00001.m4s", "2026-01-01T00:00:06+00:00", 6)));

        Assert.Throws<ArgumentException>(() => PlaylistStitcher.Stitch([first, duplicate], 12, final: false));
    }

    [Fact]
    public void A_stitched_final_index_is_final()
    {
        var part = new PartPlaylist(1, Playlist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6)));

        Assert.True(PlaylistStitcher.IsFinalIndex(PlaylistStitcher.Stitch([part], 12, final: true)));
        Assert.False(PlaylistStitcher.IsFinalIndex(PlaylistStitcher.Stitch([part], 12, final: false)));
    }

    [Fact]
    public void An_index_with_endlist_but_no_segments_is_not_final()
    {
        Assert.False(PlaylistStitcher.IsFinalIndex(PlaylistStitcher.Stitch([], 12, final: true)));
    }

    [Fact]
    public void A_malformed_index_cannot_be_judged_final()
    {
        string index = Lines("#EXTM3U", "#EXTINF:abc,", "p001_00000.m4s", "#EXT-X-ENDLIST", "");

        Assert.Throws<FormatException>(() => PlaylistStitcher.IsFinalIndex(index));
    }

    [Fact]
    public void Referenced_media_lists_init_maps_and_segment_uris()
    {
        string content = Playlist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6),
            Segment("p001_00002.m4s", "2026-01-01T00:00:06+00:00", 6));

        Assert.Equal(
            ["init_001.mp4", "p001_00001.m4s", "p001_00002.m4s"],
            PlaylistStitcher.ReferencedMedia(content).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Referenced_media_keeps_an_unterminated_segment_line_so_recovery_never_deletes_it()
    {
        // Stricter than ParsePart on purpose: this set decides what recovery may delete, so a
        // segment whose playlist line was still being written stays referenced.
        string content = "#EXTM3U\n#EXT-X-MAP:URI=\"init_001.mp4\"\n#EXTINF:6,\np001_00000.m4s\n"
            + "#EXTINF:6,\np001_00001.m4s";

        Assert.Contains("p001_00001.m4s", PlaylistStitcher.ReferencedMedia(content));
        Assert.Empty(PlaylistStitcher.ParsePart(1, content).BodyLines.Where(line => line == "p001_00001.m4s"));
    }

    [Fact]
    public void Referenced_media_reads_an_init_map_that_carries_attributes()
    {
        string content = "#EXTM3U\n#EXT-X-MAP:URI=\"init_001.mp4\",BYTERANGE=\"3@0\"\n"
            + "#EXTINF:6,\np001_00000.m4s\n";

        Assert.Equal(
            ["init_001.mp4", "p001_00000.m4s"],
            PlaylistStitcher.ReferencedMedia(content).Order(StringComparer.Ordinal));
        Assert.Equal(
            new SegmentFiles("init_001.mp4", "p001_00000.m4s"),
            PlaylistTimeline.Parse(content).SegmentAt(1));
    }

    [Fact]
    public void Referenced_media_ignores_an_unclosed_init_map()
    {
        string content = "#EXTM3U\n#EXT-X-MAP:URI=\"init_001.mp4\n#EXTINF:6,\np001_00000.m4s\n";

        Assert.Equal(["p001_00000.m4s"], PlaylistStitcher.ReferencedMedia(content));
    }
}
