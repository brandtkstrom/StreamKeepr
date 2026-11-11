using System.Globalization;
using StreamKeepr.Infrastructure.Storage;
using static StreamKeepr.Tests.Storage.PlaylistTestData;

namespace StreamKeepr.Tests.Storage;

public sealed class PlaylistChapterTests
{
    private static readonly DateTimeOffset SessionStart =
        DateTimeOffset.Parse("2026-01-01T00:00:00+00:00", CultureInfo.InvariantCulture);

    private static string StitchedPlaylist(params string[][] segments) =>
        PlaylistStitcher.Stitch(
            [new PartPlaylist(1, Playlist(12, "init_001.mp4", segments))],
            12,
            final: true);

    [Fact]
    public void Chapters_skip_the_initial_metadata_entry()
    {
        string playlist = StitchedPlaylist(
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6),
            Segment("p001_00002.m4s", "2026-01-01T00:00:06+00:00", 6));

        var metadata = new[]
        {
            new MetadataChange(SessionStart.AddSeconds(3), "Start", "Game"),
        };

        Assert.Empty(PlaylistStitcher.Chapters(playlist, metadata));
    }

    [Fact]
    public void Chapters_begin_at_the_first_metadata_change_after_the_initial_entry()
    {
        string playlist = StitchedPlaylist(
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6),
            Segment("p001_00002.m4s", "2026-01-01T00:00:06+00:00", 6));

        var metadata = new[]
        {
            new MetadataChange(SessionStart, "Start", "Game"),
            new MetadataChange(SessionStart.AddSeconds(8), "Changed", "New Game"),
        };

        var chapter = Assert.Single(PlaylistStitcher.Chapters(playlist, metadata));
        Assert.Equal(6, chapter.StartSeconds, precision: 10);
        Assert.Equal("Changed", chapter.Title);
        Assert.Equal("New Game", chapter.Game);
    }

    [Fact]
    public void Chapters_place_each_change_at_the_start_of_the_segment_where_it_falls()
    {
        string playlist = StitchedPlaylist(
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6),
            Segment("p001_00002.m4s", "2026-01-01T00:00:06+00:00", 6),
            Segment("p001_00003.m4s", "2026-01-01T00:00:12+00:00", 6));

        var metadata = new[]
        {
            new MetadataChange(SessionStart, "Start", "Game"),
            new MetadataChange(SessionStart.AddSeconds(3), "First", "Game 1"),
            new MetadataChange(SessionStart.AddSeconds(7), "Second", "Game 2"),
            new MetadataChange(SessionStart.AddSeconds(13), "Third", "Game 3"),
        };

        var chapters = PlaylistStitcher.Chapters(playlist, metadata);

        Assert.Equal(3, chapters.Count);
        Assert.Equal(new double[] { 0, 6, 12 }, chapters.Select(chapter => chapter.StartSeconds));
        Assert.Equal(new[] { "First", "Second", "Third" }, chapters.Select(chapter => chapter.Title));
        Assert.Equal(new[] { "Game 1", "Game 2", "Game 3" }, chapters.Select(chapter => chapter.Game));
    }

    [Fact]
    public void Chapters_treat_the_end_of_a_segment_as_part_of_that_segment()
    {
        string playlist = StitchedPlaylist(
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6),
            Segment("p001_00002.m4s", "2026-01-01T00:00:06+00:00", 6));

        var metadata = new[]
        {
            new MetadataChange(SessionStart, "Start", "Game"),
            new MetadataChange(SessionStart.AddSeconds(6), "Boundary", "Game 1"),
            new MetadataChange(SessionStart.AddSeconds(6.001), "After boundary", "Game 2"),
        };

        var chapters = PlaylistStitcher.Chapters(playlist, metadata);

        Assert.Equal(2, chapters.Count);
        Assert.Equal(0, chapters[0].StartSeconds, precision: 10);
        Assert.Equal("Boundary", chapters[0].Title);
        Assert.Equal(6, chapters[1].StartSeconds, precision: 10);
        Assert.Equal("After boundary", chapters[1].Title);
    }

    [Fact]
    public void Chapters_place_multiple_changes_in_one_segment_at_its_start()
    {
        string playlist = StitchedPlaylist(
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6),
            Segment("p001_00002.m4s", "2026-01-01T00:00:06+00:00", 6));

        var metadata = new[]
        {
            new MetadataChange(SessionStart, "Start", "Game"),
            new MetadataChange(SessionStart.AddSeconds(1), "One", "Game 1"),
            new MetadataChange(SessionStart.AddSeconds(2), "Two", "Game 2"),
        };

        var chapters = PlaylistStitcher.Chapters(playlist, metadata);

        Assert.Equal(2, chapters.Count);
        Assert.All(chapters, chapter => Assert.Equal(0, chapter.StartSeconds, precision: 10));
        Assert.Equal(new[] { "One", "Two" }, chapters.Select(chapter => chapter.Title));
    }

    [Fact]
    public void Chapters_return_nothing_for_a_null_playlist()
    {
        var metadata = new[]
        {
            new MetadataChange(SessionStart, "Start", "Game"),
            new MetadataChange(SessionStart.AddSeconds(1), "Changed", "New Game"),
        };

        Assert.Empty(PlaylistStitcher.Chapters(null, metadata));
    }

    [Theory]
    [InlineData("#EXTM3U\n#EXTINF:not-a-number,\nsegment.m4s")]
    [InlineData("#EXTM3U\nsegment.m4s")]
    [InlineData("#EXTM3U\n#EXT-X-PROGRAM-DATE-TIME:not-a-date\n#EXTINF:6,\nsegment.m4s")]
    public void Chapters_return_nothing_for_an_unparseable_playlist(string playlist)
    {
        var metadata = new[]
        {
            new MetadataChange(SessionStart, "Start", "Game"),
            new MetadataChange(SessionStart.AddSeconds(1), "Changed", "New Game"),
        };

        Assert.Empty(PlaylistStitcher.Chapters(playlist, metadata));
    }

    [Fact]
    public void Chapters_compare_absolute_instants_across_time_zone_offsets()
    {
        string playlist = StitchedPlaylist(
            Segment("p001_00001.m4s", "2026-01-15T10:00:00+02:00", 10),
            Segment("p001_00002.m4s", "2026-01-15T10:00:10+02:00", 10));

        var metadata = new[]
        {
            new MetadataChange(DateTimeOffset.Parse("2026-01-15T08:00:00Z", CultureInfo.InvariantCulture), "Start", "Game"),
            new MetadataChange(DateTimeOffset.Parse("2026-01-15T08:00:05Z", CultureInfo.InvariantCulture), "First", "Game 1"),
            new MetadataChange(DateTimeOffset.Parse("2026-01-15T08:00:12Z", CultureInfo.InvariantCulture), "Second", "Game 2"),
        };

        var chapters = PlaylistStitcher.Chapters(playlist, metadata);

        Assert.Equal(2, chapters.Count);
        Assert.Equal(0, chapters[0].StartSeconds, precision: 10);
        Assert.Equal("First", chapters[0].Title);
        Assert.Equal(10, chapters[1].StartSeconds, precision: 10);
        Assert.Equal("Second", chapters[1].Title);
    }

    [Fact]
    public void Chapters_use_absolute_instants_across_the_dst_fall_back_boundary()
    {
        // 01:30-04:00 is 05:30Z. One hour later the wall clock is 01:30-05:00 (06:30Z),
        // after the clocks fall back, so the change at 01:31-05:00 belongs to part two.
        string playlist = StitchedPlaylist(
            Segment("p001_00001.m4s", "2026-11-01T01:30:00-04:00", 3600),
            Segment("p001_00002.m4s", "2026-11-01T01:30:00-05:00", 600));

        var metadata = new[]
        {
            new MetadataChange(
                DateTimeOffset.Parse("2026-11-01T01:30:00-04:00", CultureInfo.InvariantCulture),
                "Start",
                "Game"),
            new MetadataChange(
                DateTimeOffset.Parse("2026-11-01T01:31:00-05:00", CultureInfo.InvariantCulture),
                "After fall back",
                "Game 1"),
        };

        var chapter = Assert.Single(PlaylistStitcher.Chapters(playlist, metadata));

        Assert.Equal(3600, chapter.StartSeconds, precision: 10);
        Assert.Equal("After fall back", chapter.Title);
    }

    [Fact]
    public void Chapters_use_absolute_instants_across_the_dst_spring_forward_boundary()
    {
        // 01:50-05:00 is 06:50Z. The 20-minute segment ends at 03:10-04:00 (07:10Z),
        // after the clocks spring forward.
        string playlist = StitchedPlaylist(
            Segment("p001_00001.m4s", "2026-03-08T01:50:00-05:00", 1200),
            Segment("p001_00002.m4s", "2026-03-08T03:10:00-04:00", 600));

        var metadata = new[]
        {
            new MetadataChange(
                DateTimeOffset.Parse("2026-03-08T01:50:00-05:00", CultureInfo.InvariantCulture),
                "Start",
                "Game"),
            new MetadataChange(
                DateTimeOffset.Parse("2026-03-08T03:05:00-04:00", CultureInfo.InvariantCulture),
                "Before spring forward",
                "Game 1"),
            new MetadataChange(
                DateTimeOffset.Parse("2026-03-08T03:15:00-04:00", CultureInfo.InvariantCulture),
                "After spring forward",
                "Game 2"),
        };

        var chapters = PlaylistStitcher.Chapters(playlist, metadata);

        Assert.Equal(2, chapters.Count);
        Assert.Equal(0, chapters[0].StartSeconds, precision: 10);
        Assert.Equal("Before spring forward", chapters[0].Title);
        Assert.Equal(1200, chapters[1].StartSeconds, precision: 10);
        Assert.Equal("After spring forward", chapters[1].Title);
    }

    [Fact]
    public void Chapters_use_the_cumulative_stitched_timeline_across_a_discontinuity()
    {
        var first = new PartPlaylist(1, Playlist(12, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-01-01T00:00:00+00:00", 6)));
        var second = new PartPlaylist(2, Playlist(12, "init_002.mp4",
            Segment("p002_00001.m4s", "2026-01-01T01:00:00+00:00", 6)));
        string playlist = PlaylistStitcher.Stitch([first, second], 12, final: true);

        var metadata = new[]
        {
            new MetadataChange(SessionStart, "Start", "Game"),
            new MetadataChange(SessionStart.AddHours(1).AddSeconds(1), "After the gap", "Game 1"),
        };

        var chapter = Assert.Single(PlaylistStitcher.Chapters(playlist, metadata));

        Assert.Equal(6, chapter.StartSeconds, precision: 10);
        Assert.Equal("After the gap", chapter.Title);
    }
}
