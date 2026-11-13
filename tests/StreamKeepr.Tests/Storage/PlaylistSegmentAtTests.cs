using StreamKeepr.Infrastructure.Storage;
using static StreamKeepr.Tests.Storage.PlaylistTestData;

namespace StreamKeepr.Tests.Storage;

public sealed class PlaylistSegmentAtTests
{
    private const string Pdt = "2026-11-10T18:30:00.000+00:00";

    private static readonly string TwoParts = Lines(
        "#EXTM3U",
        "#EXT-X-MAP:URI=\"init_001.mp4\"",
        "#EXTINF:6,",
        "p001_00000.m4s",
        "#EXTINF:6,",
        "p001_00001.m4s",
        "#EXT-X-DISCONTINUITY",
        "#EXT-X-MAP:URI=\"init_002.mp4\"",
        "#EXTINF:4,",
        "p002_00000.m4s");

    [Theory]
    [InlineData(0, "init_001.mp4", "p001_00000.m4s")]
    [InlineData(5.9, "init_001.mp4", "p001_00000.m4s")]
    [InlineData(6, "init_001.mp4", "p001_00001.m4s")]
    [InlineData(12, "init_002.mp4", "p002_00000.m4s")]
    [InlineData(15.9, "init_002.mp4", "p002_00000.m4s")]
    public void Segment_at_returns_the_covering_segment_with_its_parts_init_map(
        double offset, string init, string media)
    {
        Assert.Equal(new SegmentFiles(init, media), PlaylistStitcher.SegmentAt(TwoParts, offset));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(100)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Segment_at_returns_null_outside_the_timeline(double offset)
    {
        Assert.Null(PlaylistStitcher.SegmentAt(TwoParts, offset));
    }

    [Fact]
    public void Segment_at_reads_a_part_playlist_with_program_date_times()
    {
        string playlist = Playlist(6, "init_003.mp4", Segment("p003_00000.m4s", Pdt, 6), Segment("p003_00001.m4s", Pdt, 6));

        Assert.Equal(new SegmentFiles("init_003.mp4", "p003_00001.m4s"), PlaylistStitcher.SegmentAt(playlist, 7));
    }

    [Fact]
    public void Segment_at_returns_null_when_the_segment_has_no_init_map()
    {
        string playlist = Lines("#EXTM3U", "#EXTINF:6,", "p001_00000.m4s");

        Assert.Null(PlaylistStitcher.SegmentAt(playlist, 1));
    }

    [Theory]
    [InlineData("../other/init_001.mp4", "p001_00000.m4s")]
    [InlineData("init_001.mp4", "/etc/passwd")]
    [InlineData("init_001.mp4", "sub/p001_00000.m4s")]
    public void Segment_at_refuses_names_that_leave_the_session_folder(string init, string media)
    {
        string playlist = Lines("#EXTM3U", $"#EXT-X-MAP:URI=\"{init}\"", "#EXTINF:6,", media);

        Assert.Null(PlaylistStitcher.SegmentAt(playlist, 1));
    }

    [Fact]
    public void Segment_at_returns_null_for_a_malformed_playlist()
    {
        string playlist = Lines("#EXTM3U", "#EXT-X-MAP:URI=\"init_001.mp4\"", "#EXTINF:abc,", "p001_00000.m4s");

        Assert.Null(PlaylistStitcher.SegmentAt(playlist, 1));
    }
}
