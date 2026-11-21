using StreamKeepr.Infrastructure.Storage;
using static StreamKeepr.Tests.Storage.PlaylistTestData;

namespace StreamKeepr.Tests.Storage;

/// <summary>
/// Duration, segment count, and program-date-time of a parsed playlist. Chapters and
/// segment lookup have their own fixtures in <see cref="PlaylistChapterTests"/> and
/// <see cref="PlaylistSegmentAtTests"/>.
/// </summary>
public sealed class PlaylistTimelineTests
{
    [Theory]
    [InlineData(6, 4, 5.5, 4.5, 20)]
    [InlineData(6, 6, 6, 6, 24)]
    public void Duration_is_the_sum_of_segment_durations(
        double firstPartDuration1,
        double firstPartDuration2,
        double secondPartDuration1,
        double secondPartDuration2,
        double expected)
    {
        var first = new PartPlaylist(1, Playlist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", firstPartDuration1),
            Segment("p001_00002.m4s", "2026-01-01T00:00:06+00:00", firstPartDuration2)));
        var second = new PartPlaylist(2, Playlist(6, "init_002.mp4",
            Segment("p002_00001.m4s", "2026-01-01T00:00:12+00:00", secondPartDuration1),
            Segment("p002_00002.m4s", "2026-01-01T00:00:18+00:00", secondPartDuration2)));
        string stitched = PlaylistStitcher.Stitch([first, second], 12, final: true);

        Assert.Equal(expected, PlaylistTimeline.Parse(stitched).Duration, precision: 10);
    }

    [Fact]
    public void Segment_count_counts_segment_uris_and_ignores_init_maps_and_tags()
    {
        var first = new PartPlaylist(1, Playlist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6),
            Segment("p001_00002.m4s", "2026-01-01T00:00:06+00:00", 6)));
        var second = new PartPlaylist(2, Playlist(6, "init_002.mp4",
            Segment("p002_00001.m4s", "2026-01-01T00:00:12+00:00", 6)));

        string stitched = PlaylistStitcher.Stitch([first, second], 12, final: true);

        Assert.Equal(2, PlaylistTimeline.Parse(first.Content).SegmentCount);
        Assert.Equal(3, PlaylistTimeline.Parse(stitched).SegmentCount);
    }

    [Fact]
    public void A_trailing_extinf_without_a_uri_is_not_a_segment()
    {
        string playlist = Lines(
            "#EXTM3U",
            "#EXTINF:6,",
            "segment.m4s",
            "#EXTINF:6,");

        PlaylistTimeline timeline = PlaylistTimeline.Parse(playlist);

        Assert.Equal(1, timeline.SegmentCount);
        Assert.Equal(6, timeline.Duration, precision: 10);
    }

    [Fact]
    public void An_empty_playlist_has_no_segments_and_no_start()
    {
        PlaylistTimeline timeline = PlaylistTimeline.Parse("#EXTM3U\n");

        Assert.Equal(0, timeline.SegmentCount);
        Assert.Equal(0, timeline.Duration);
        Assert.Null(timeline.FirstProgramDateTime);
    }

    [Fact]
    public void First_program_date_time_is_the_first_one_on_the_timeline()
    {
        string playlist = Playlist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+02:00", 6),
            Segment("p001_00002.m4s", "2026-01-01T00:00:06+02:00", 6));

        Assert.Equal(
            new DateTimeOffset(2025, 12, 31, 22, 0, 0, TimeSpan.Zero),
            PlaylistTimeline.Parse(playlist).FirstProgramDateTime);
    }

    [Fact]
    public void A_program_date_time_without_an_offset_is_read_as_utc()
    {
        string playlist = Lines(
            "#EXTM3U",
            "#EXT-X-PROGRAM-DATE-TIME:2026-01-01T00:00:00",
            "#EXTINF:6,",
            "segment.m4s");

        Assert.Equal(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            PlaylistTimeline.Parse(playlist).FirstProgramDateTime);
    }

    [Theory]
    [InlineData("#EXTM3U\n#EXTINF:not-a-number,\nsegment.m4s")]
    [InlineData("#EXTM3U\n#EXTINF:-1,\nsegment.m4s")]
    [InlineData("#EXTM3U\nsegment.m4s")]
    [InlineData("#EXTM3U\n#EXTINF:6,\nfirst.m4s\nsecond.m4s")]
    [InlineData("#EXTM3U\n#EXT-X-PROGRAM-DATE-TIME:not-a-date\n#EXTINF:6,\nsegment.m4s")]
    public void Parse_rejects_a_malformed_playlist_and_try_parse_returns_null(string playlist)
    {
        Assert.Throws<FormatException>(() => PlaylistTimeline.Parse(playlist));
        Assert.Null(PlaylistTimeline.TryParse(playlist));
    }

    [Fact]
    public void Try_parse_returns_null_for_a_missing_playlist()
    {
        Assert.Null(PlaylistTimeline.TryParse(null));
    }

    [Fact]
    public void Try_parse_reads_a_well_formed_playlist()
    {
        string playlist = Lines("#EXTM3U", "#EXTINF:4.5,", "segment.m4s");

        PlaylistTimeline? timeline = PlaylistTimeline.TryParse(playlist);

        Assert.NotNull(timeline);
        Assert.Equal(1, timeline.SegmentCount);
        Assert.Equal(4.5, timeline.Duration, precision: 10);
    }
}
