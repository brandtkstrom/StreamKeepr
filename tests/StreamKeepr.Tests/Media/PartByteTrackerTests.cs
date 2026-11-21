using StreamKeepr.Infrastructure.Media;

namespace StreamKeepr.Tests.Media;

public sealed class PartByteTrackerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "streamkeepr-tests",
        Guid.NewGuid().ToString("N"));

    public PartByteTrackerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void A_missing_playlist_counts_nothing_and_does_not_throw()
    {
        var tracker = new PartByteTracker(_directory, 1);

        Assert.Equal(0, tracker.Sample());
        Assert.Equal(0, tracker.Sample());
        Assert.Equal(0, tracker.Total);
    }

    [Fact]
    public void An_empty_playlist_counts_nothing()
    {
        Write(PartFiles.Playlist(1), string.Empty);

        var tracker = new PartByteTracker(_directory, 1);

        Assert.Equal(0, tracker.Sample());
        Assert.Equal(0, tracker.Sample());
    }

    [Fact]
    public void The_init_is_counted_even_when_the_playlist_is_missing()
    {
        Write(PartFiles.Init(1), "init");

        var tracker = new PartByteTracker(_directory, 1);

        Assert.Equal(4, tracker.Sample());
        Assert.Equal(4, tracker.Sample());
    }

    [Fact]
    public void The_init_is_counted_when_the_map_tag_is_malformed()
    {
        Write(PartFiles.Init(1), "init");
        Write(PartFiles.Playlist(1), "#EXTM3U\n#EXT-X-MAP:URI=\"init_001.mp4\n");

        var tracker = new PartByteTracker(_directory, 1);

        Assert.Equal(4, tracker.Sample());
    }

    [Fact]
    public void Out_of_order_and_duplicate_segments_are_counted_once()
    {
        Write(PartFiles.Init(1), "init");
        Write("p001_00000.m4s", "aaa");
        Write("p001_00001.m4s", "bbbb");
        Write(
            PartFiles.Playlist(1),
            "#EXTM3U\n#EXT-X-MAP:URI=\"init_001.mp4\"\np001_00001.m4s\np001_00000.m4s\np001_00001.m4s\n");

        var tracker = new PartByteTracker(_directory, 1);

        Assert.Equal(11, tracker.Sample());
        Assert.Equal(11, tracker.Sample());
    }

    [Fact]
    public void Temporary_files_and_duplicates_are_ignored()
    {
        Write(PartFiles.Init(1), "init");
        Write("p001_00000.m4s", "aa");
        Write("p001_00001.m4s.tmp", "this is a partial write");
        Write("init_001.mp4.tmp", "partial init");
        Write(
            PartFiles.Playlist(1),
            """
            #EXTM3U
            #EXT-X-MAP:URI="init_001.mp4.tmp"
            p001_00000.m4s
            p001_00000.m4s
            p001_00001.m4s.tmp
            """);

        var tracker = new PartByteTracker(_directory, 1);

        Assert.Equal(6, tracker.Sample());
        Assert.Equal(6, tracker.Sample());
    }

    [Fact]
    public void A_segment_appearing_after_the_first_sample_is_added_once()
    {
        Write(PartFiles.Init(1), "init");
        Write("p001_00000.m4s", "aa");
        Write(PartFiles.Playlist(1), Playlist("p001_00000.m4s"));

        var tracker = new PartByteTracker(_directory, 1);
        Assert.Equal(6, tracker.Sample());

        Write("p001_00001.m4s", "bbb");
        File.AppendAllText(
            Path.Combine(_directory, PartFiles.Playlist(1)),
            "\np001_00001.m4s\n");

        Assert.Equal(9, tracker.Sample());
        Assert.Equal(9, tracker.Sample());
    }

    [Fact]
    public void A_missing_listed_segment_is_retried_without_hiding_later_segments()
    {
        Write(PartFiles.Init(1), "init");
        Write("p001_00001.m4s", "bb");
        Write(
            PartFiles.Playlist(1),
            Playlist("p001_00000.m4s", "p001_00001.m4s"));

        var tracker = new PartByteTracker(_directory, 1);
        Assert.Equal(6, tracker.Sample());

        Write("p001_00000.m4s", "aaaa");
        Assert.Equal(10, tracker.Sample());
    }

    [Fact]
    public void Deleting_a_segment_keeps_the_live_total()
    {
        Write(PartFiles.Init(1), "init");
        Write("p001_00000.m4s", "aaaa");
        Write(PartFiles.Playlist(1), Playlist("p001_00000.m4s"));

        var tracker = new PartByteTracker(_directory, 1);
        Assert.Equal(8, tracker.Sample());

        File.Delete(Path.Combine(_directory, "p001_00000.m4s"));

        Assert.Equal(8, tracker.Sample());
    }

    [Fact]
    public void A_malformed_or_truncated_playlist_keeps_the_last_total()
    {
        Write(PartFiles.Init(1), "init");
        Write("p001_00000.m4s", "aaaa");
        Write(PartFiles.Playlist(1), Playlist("p001_00000.m4s"));

        var tracker = new PartByteTracker(_directory, 1);
        Assert.Equal(8, tracker.Sample());

        Write(
            PartFiles.Playlist(1),
            "#EXTM3U\n#EXT-X-MAP:URI=\"init_001.mp4\n#EXTINF:2,\np001_0000");

        Assert.Equal(8, tracker.Sample());
    }

    [Fact]
    public void Wrong_part_and_path_traversal_names_are_rejected()
    {
        Write(PartFiles.Init(1), "init");
        Write("p001_00000.m4s", "ok");
        Write("p002_00000.m4s", "other part");
        Write("p001_00000.m4s.tmp", "partial");
        Write("p001_0000.m4s", "too few digits");
        Write(PartFiles.Playlist(1), """
            #EXTM3U
            #EXT-X-MAP:URI="init_001.mp4"
            p001_00000.m4s
            p002_00000.m4s
            ../p002_00000.m4s
            sub/p001_00000.m4s
            p001_00000.m4s.tmp
            p001_0000.m4s
            init_002.mp4
            """);

        var tracker = new PartByteTracker(_directory, 1);

        Assert.Equal(6, tracker.Sample());
    }

    [Fact]
    public void Parts_are_isolated_and_a_second_part_starts_at_zero()
    {
        Write(PartFiles.Init(1), "init");
        Write("p001_00000.m4s", "one");
        Write(PartFiles.Init(2), "init2");
        Write("p002_00000.m4s", "two!");
        Write(
            PartFiles.Playlist(1),
            "#EXTM3U\n#EXT-X-MAP:URI=\"init_001.mp4\"\np001_00000.m4s\np002_00000.m4s\ninit_002.mp4\n");
        Write(
            PartFiles.Playlist(2),
            "#EXTM3U\n#EXT-X-MAP:URI=\"init_002.mp4\"\np002_00000.m4s\np001_00000.m4s\n");

        var partOne = new PartByteTracker(_directory, 1);
        var partTwo = new PartByteTracker(_directory, 2);

        Assert.Equal(7, partOne.Sample());
        Assert.Equal(0, partTwo.Total);
        Assert.Equal(9, partTwo.Sample());
        Assert.Equal(7, partOne.Total);
    }

    [Fact]
    public void A_transient_read_failure_returns_the_last_total_and_retries()
    {
        Write(PartFiles.Init(1), "init");
        Write("p001_00000.m4s", "aaaa");
        Write(PartFiles.Playlist(1), Playlist("p001_00000.m4s"));

        var probe = new ProbeSource(PhysicalPartByteSource.Instance);
        var tracker = new PartByteTracker(_directory, 1, probe);
        Assert.Equal(8, tracker.Sample());

        Write("p001_00001.m4s", "bb");
        File.AppendAllText(
            Path.Combine(_directory, PartFiles.Playlist(1)),
            "\np001_00001.m4s\n");
        probe.ReadFailuresRemaining = 1;

        Assert.Equal(8, tracker.Sample());
        Assert.Equal(10, tracker.Sample());
    }

    [Fact]
    public void A_transient_stat_failure_returns_the_last_total_and_retries()
    {
        Write(PartFiles.Init(1), "init");
        Write("p001_00000.m4s", "aaaa");
        Write(PartFiles.Playlist(1), Playlist("p001_00000.m4s"));

        var probe = new ProbeSource(PhysicalPartByteSource.Instance);
        var tracker = new PartByteTracker(_directory, 1, probe);
        Assert.Equal(8, tracker.Sample());

        Write("p001_00001.m4s", "bb");
        File.AppendAllText(
            Path.Combine(_directory, PartFiles.Playlist(1)),
            "\np001_00001.m4s\n");
        probe.FailLengthFor = "p001_00001.m4s";
        probe.LengthFailuresRemaining = 1;

        Assert.Equal(8, tracker.Sample());
        Assert.Equal(10, tracker.Sample());
    }

    [Fact]
    public void An_unlisted_segment_is_missed_by_the_tracker_but_counted_by_the_full_scan()
    {
        Write(PartFiles.Init(1), "init");
        Write("p001_00000.m4s", "aaaa");
        Write(PartFiles.Playlist(1), Playlist("p001_00000.m4s"));
        // An unlisted segment still counts on disk; the part-end scan is authoritative.
        Write("p001_00001.m4s", "bb");

        var tracker = new PartByteTracker(_directory, 1);
        Assert.Equal(8, tracker.Sample());

        Assert.Equal(10, PartFiles.TotalBytes(_directory, 1));
    }

    [Fact]
    public void Only_newly_seen_segments_are_stat_ed()
    {
        Write(PartFiles.Init(1), "init");
        Write("p001_00000.m4s", "aa");
        Write("p001_00001.m4s", "bb");
        Write("p001_00002.m4s", "cc");
        Write(
            PartFiles.Playlist(1),
            Playlist("p001_00000.m4s", "p001_00001.m4s", "p001_00002.m4s"));

        var probe = new ProbeSource(PhysicalPartByteSource.Instance);
        var tracker = new PartByteTracker(_directory, 1, probe);

        Assert.Equal(10, tracker.Sample());
        Assert.Equal(3, probe.SegmentLengthCalls());

        Assert.Equal(10, tracker.Sample());
        int callsAfterFirstSample = probe.SegmentLengthCalls();
        Assert.Equal(3, callsAfterFirstSample);

        Assert.Equal(10, tracker.Sample());
        Assert.Equal(callsAfterFirstSample, probe.SegmentLengthCalls());

        Write("p001_00003.m4s", "dddd");
        File.AppendAllText(
            Path.Combine(_directory, PartFiles.Playlist(1)),
            "\np001_00003.m4s\n");

        Assert.Equal(14, tracker.Sample());
        Assert.Equal(callsAfterFirstSample + 1, probe.SegmentLengthCalls());
    }

    private static string Playlist(params string[] segments) =>
        "#EXTM3U\n#EXT-X-MAP:URI=\"init_001.mp4\"\n" + string.Join('\n', segments) + "\n";

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_directory, name), content);

    private sealed class ProbeSource(IPartByteSource inner) : IPartByteSource
    {
        private readonly List<string> _lengthCalls = [];

        public int ReadFailuresRemaining { get; set; }

        public int LengthFailuresRemaining { get; set; }

        public string? FailLengthFor { get; set; }

        public int SegmentLengthCalls() =>
            _lengthCalls.Count(path => path.EndsWith(".m4s", StringComparison.Ordinal));

        public PlaylistStamp? GetPlaylistStamp(string path) => inner.GetPlaylistStamp(path);

        public string ReadPlaylist(string path)
        {
            if (ReadFailuresRemaining > 0)
            {
                ReadFailuresRemaining--;
                throw new IOException("simulated transient playlist read failure");
            }

            return inner.ReadPlaylist(path);
        }

        public long GetFileLength(string path)
        {
            _lengthCalls.Add(path);

            string name = Path.GetFileName(path);
            if (LengthFailuresRemaining > 0
                && (FailLengthFor is null || name == FailLengthFor))
            {
                LengthFailuresRemaining--;
                throw new IOException("simulated transient stat failure");
            }

            return inner.GetFileLength(path);
        }
    }
}
