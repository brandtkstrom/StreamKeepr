using System.Text;
using StreamKeepr.Infrastructure.Storage;
using static StreamKeepr.Tests.Storage.PlaylistTestData;

namespace StreamKeepr.Tests.Storage;

public sealed class LivePlaylistComposerTests
{
    private const int MinTarget = 12;

    [Fact]
    public async Task Warm_refresh_reads_no_content_and_reuses_the_snapshot()
    {
        string part = LivePlaylist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        var source = new RecordingSource().Add(1, part);

        LiveSnapshot first = await BuildAsync(null, [Observe(1, part)], source);
        source.Reset();

        LiveSnapshot second = await BuildAsync(first, [Observe(1, part)], source);

        Assert.Same(first, second);
        Assert.Empty(source.Reads);
    }

    [Fact]
    public async Task A_grown_last_part_is_the_only_part_read_and_equals_a_full_stitch()
    {
        string first = LivePlaylist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        var source = new RecordingSource().Add(1, first);
        LiveSnapshot before = await BuildAsync(null, [Observe(1, first, ticks: 1)], source);

        string appended = Append(first,
            Segment("p001_00002.m4s", "2026-11-10T18:30:06Z", 6));
        source.Add(1, appended);
        source.Reset();

        LiveSnapshot after = await BuildAsync(before, [Observe(1, appended, ticks: 2)], source);

        Assert.Equal([SourcePath(1)], source.Reads);
        Assert.Equal(Expected((1, appended)), after.Body);
        Assert.Contains("p001_00002.m4s", after.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unterminated_tail_is_never_exposed_and_keeps_the_last_good_body()
    {
        string first = LivePlaylist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        var source = new RecordingSource().Add(1, first);
        LiveSnapshot before = await BuildAsync(null, [Observe(1, first, ticks: 1)], source);

        // A line feed has not arrived yet, so the URI/tag tail is still being written.
        string partial = first + "#EXTINF:6,\np001_00002.m4s";
        source.Add(1, partial);
        source.Reset();

        LiveSnapshot after = await BuildAsync(before, [Observe(1, partial, ticks: 2)], source);

        Assert.Equal(before.Body, after.Body);
        Assert.DoesNotContain("p001_00002.m4s", after.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_part_cut_off_mid_write_never_shrinks_the_live_index()
    {
        string first = LivePlaylist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6),
            Segment("p001_00002.m4s", "2026-11-10T18:30:06Z", 6));
        var source = new RecordingSource().Add(1, first);
        LiveSnapshot before = await BuildAsync(null, [Observe(1, first, ticks: 1)], source);

        // A read that raced a non-atomic write sees only a prefix of the file, cut mid-line.
        string cut = first[..first.IndexOf("p001_00002", StringComparison.Ordinal)] + "p001_0";
        source.Add(1, cut);

        LiveSnapshot after = await BuildAsync(before, [Observe(1, cut, ticks: 2)], source);

        Assert.Same(before, after);
        Assert.Contains("p001_00002.m4s", after.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_complete_but_tag_only_tail_changes_no_body_bytes()
    {
        string first = LivePlaylist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        var source = new RecordingSource().Add(1, first);
        LiveSnapshot before = await BuildAsync(null, [Observe(1, first, ticks: 1)], source);

        string tagOnly = first + "#EXTINF:6,\n";
        source.Add(1, tagOnly);
        source.Reset();

        LiveSnapshot after = await BuildAsync(before, [Observe(1, tagOnly, ticks: 2)], source);

        Assert.Equal(before.Body, after.Body);
        Assert.Equal(1, Occurrences(after.Body, "#EXTINF:"));
    }

    [Fact]
    public async Task A_truncated_last_part_drops_its_removed_segments()
    {
        string first = LivePlaylist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6),
            Segment("p001_00002.m4s", "2026-11-10T18:30:06Z", 6));
        var source = new RecordingSource().Add(1, first);
        LiveSnapshot before = await BuildAsync(null, [Observe(1, first, ticks: 1)], source);

        string truncated = LivePlaylist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        source.Add(1, truncated);
        source.Reset();

        LiveSnapshot after = await BuildAsync(before, [Observe(1, truncated, ticks: 2)], source);

        Assert.Equal([SourcePath(1)], source.Reads);
        Assert.Equal(Expected((1, truncated)), after.Body);
        Assert.DoesNotContain("p001_00002.m4s", after.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_longer_part_target_raises_the_header_target()
    {
        string first = LivePlaylist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        var source = new RecordingSource().Add(1, first);
        LiveSnapshot before = await BuildAsync(null, [Observe(1, first, ticks: 1)], source);

        string longerTarget = LivePlaylist(20, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        source.Add(1, longerTarget);
        source.Reset();

        LiveSnapshot after = await BuildAsync(before, [Observe(1, longerTarget, ticks: 2)], source);

        Assert.Equal([SourcePath(1)], source.Reads);
        Assert.Contains("#EXT-X-TARGETDURATION:20", after.Body, StringComparison.Ordinal);
        Assert.Equal(Expected((1, longerTarget)), after.Body);
    }

    [Fact]
    public async Task A_new_last_part_adds_one_discontinuity_before_its_map()
    {
        string first = LivePlaylist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        string second = LivePlaylist(6, "init_002.mp4",
            Segment("p002_00001.m4s", "2026-11-10T18:30:06Z", 6));
        var source = new RecordingSource().Add(1, first).Add(2, second);
        LiveSnapshot before = await BuildAsync(null, [Observe(1, first)], source);

        source.Reset();
        LiveSnapshot after = await BuildAsync(
            before, [Observe(1, first), Observe(2, second)], source);

        Assert.Equal([SourcePath(2)], source.Reads);
        Assert.Equal(Expected((1, first), (2, second)), after.Body);
        Assert.Equal(1, Occurrences(after.Body, "#EXT-X-DISCONTINUITY"));
        Assert.True(
            after.Body.IndexOf("#EXT-X-DISCONTINUITY", StringComparison.Ordinal)
            < after.Body.IndexOf("#EXT-X-MAP:URI=\"init_002.mp4\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_previous_last_part_is_read_once_more_when_a_new_part_appears()
    {
        string first = LivePlaylist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        var source = new RecordingSource().Add(1, first);
        LiveSnapshot before = await BuildAsync(null, [Observe(1, first, ticks: 1)], source);

        // Part 1 received its final segment just before it closed and part 2 opened.
        string closed = Append(first,
            Segment("p001_00002.m4s", "2026-11-10T18:30:06Z", 6));
        string second = LivePlaylist(6, "init_002.mp4",
            Segment("p002_00001.m4s", "2026-11-10T18:30:12Z", 6));
        source.Add(1, closed).Add(2, second);
        source.Reset();

        LiveSnapshot after = await BuildAsync(
            before, [Observe(1, closed, ticks: 2), Observe(2, second)], source);

        Assert.Equal([SourcePath(1), SourcePath(2)], source.Reads);
        Assert.Equal(Expected((1, closed), (2, second)), after.Body);
    }

    [Fact]
    public async Task An_empty_new_part_adds_nothing()
    {
        string first = LivePlaylist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        string empty = LivePlaylist(6, "init_002.mp4");
        var source = new RecordingSource().Add(1, first).Add(2, empty);
        LiveSnapshot before = await BuildAsync(null, [Observe(1, first)], source);

        source.Reset();
        LiveSnapshot after = await BuildAsync(
            before, [Observe(1, first), Observe(2, empty)], source);

        Assert.Equal([SourcePath(2)], source.Reads);
        Assert.Equal(before.Body, after.Body);
        Assert.DoesNotContain("#EXT-X-DISCONTINUITY", after.Body, StringComparison.Ordinal);
        Assert.Equal(Expected((1, first), (2, empty)), after.Body);
    }

    [Fact]
    public async Task An_appended_earlier_part_is_ignored_and_keeps_the_cached_body()
    {
        string first = LivePlaylist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        string second = LivePlaylist(6, "init_002.mp4",
            Segment("p002_00001.m4s", "2026-11-10T18:30:06Z", 6));
        var source = new RecordingSource().Add(1, first).Add(2, second);
        LiveSnapshot before = await BuildAsync(
            null, [Observe(1, first), Observe(2, second)], source);

        string appended = Append(first,
            Segment("p001_99999.m4s", "2026-11-10T18:30:00Z", 6));
        source.Add(1, appended);
        source.Reset();

        LiveSnapshot after = await BuildAsync(
            before, [Observe(1, appended, ticks: 2), Observe(2, second)], source);

        Assert.Empty(source.Reads);
        Assert.Equal(before.Body, after.Body);
        Assert.DoesNotContain("p001_99999.m4s", after.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deleted_part_leaves_the_index_without_rereading_the_others()
    {
        string first = LivePlaylist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        string second = LivePlaylist(6, "init_002.mp4",
            Segment("p002_00001.m4s", "2026-11-10T18:30:06Z", 6));
        var source = new RecordingSource().Add(1, first).Add(2, second);
        LiveSnapshot before = await BuildAsync(
            null, [Observe(1, first), Observe(2, second)], source);

        source.Reset();
        LiveSnapshot after = await BuildAsync(before, [Observe(2, second)], source);

        Assert.Empty(source.Reads);
        Assert.Equal(Expected((2, second)), after.Body);
        Assert.DoesNotContain("p001_00001.m4s", after.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-DISCONTINUITY", after.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_inserted_part_is_read_and_the_others_come_from_memory()
    {
        string first = LivePlaylist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        string second = LivePlaylist(6, "init_002.mp4",
            Segment("p002_00001.m4s", "2026-11-10T18:30:06Z", 6));
        string third = LivePlaylist(6, "init_003.mp4",
            Segment("p003_00001.m4s", "2026-11-10T18:30:18Z", 6));
        var source = new RecordingSource().Add(1, first).Add(2, second).Add(3, third);
        LiveSnapshot before = await BuildAsync(
            null, [Observe(1, first), Observe(2, second)], source);

        source.Reset();
        LiveSnapshot after = await BuildAsync(
            before, [Observe(1, first), Observe(3, third)], source);

        Assert.Equal([SourcePath(3)], source.Reads);
        Assert.Equal(Expected((1, first), (3, third)), after.Body);
    }

    [Fact]
    public async Task An_unreadable_changed_part_propagates_and_keeps_the_previous_snapshot()
    {
        string first = LivePlaylist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        var source = new RecordingSource().Add(1, first);
        LiveSnapshot before = await BuildAsync(null, [Observe(1, first, ticks: 1)], source);

        source.ThrowOn(1, new IOException("EIO"));
        string appended = Append(first,
            Segment("p001_00002.m4s", "2026-11-10T18:30:06Z", 6));
        source.Add(1, appended);

        await Assert.ThrowsAsync<IOException>(() =>
            BuildAsync(before, [Observe(1, appended, ticks: 2)], source));

        Assert.Equal(Expected((1, first)), before.Body);
        Assert.Contains("p001_00001.m4s", before.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_part_keeps_the_last_good_body_for_a_thumbnail_read()
    {
        string first = LivePlaylist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        var source = new RecordingSource().Add(1, first);
        LiveSnapshot before = await BuildAsync(null, [Observe(1, first, ticks: 1)], source);

        source.Add(1, "<html>not a playlist</html>");
        LiveSnapshot after = await BuildAsync(
            before, [Observe(1, 99, ticks: 2)], source);

        Assert.Equal(before.Body, after.Body);
        Assert.DoesNotContain("<html>", after.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sequence_of_live_changes_always_equals_a_full_stitch()
    {
        string first = LivePlaylist(6, "init_001.mp4",
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        var source = new RecordingSource().Add(1, first);
        LiveSnapshot snapshot = await BuildAsync(null, [Observe(1, first, ticks: 1)], source);
        Assert.Equal(Expected((1, first)), snapshot.Body);

        string second = Append(first,
            Segment("p001_00002.m4s", "2026-11-10T18:30:06Z", 6));
        source.Add(1, second);
        snapshot = await BuildAsync(snapshot, [Observe(1, second, ticks: 2)], source);
        Assert.Equal(Expected((1, second)), snapshot.Body);

        string newPart = LivePlaylist(6, "init_002.mp4",
            Segment("p002_00001.m4s", "2026-11-10T18:30:12Z", 6),
            Segment("p002_00002.m4s", "2026-11-10T18:30:18Z", 6));
        source.Add(2, newPart);
        snapshot = await BuildAsync(
            snapshot, [Observe(1, second), Observe(2, newPart, ticks: 2)], source);
        Assert.Equal(Expected((1, second), (2, newPart)), snapshot.Body);

        string final = Append(newPart,
            Segment("p002_00003.m4s", "2026-11-10T18:30:24Z", 6));
        source.Add(2, final);
        snapshot = await BuildAsync(
            snapshot, [Observe(1, second), Observe(2, final, ticks: 3)], source);
        Assert.Equal(Expected((1, second), (2, final)), snapshot.Body);
    }

    private static Task<LiveSnapshot> BuildAsync(
        LiveSnapshot? previous,
        PartFileObservation[] files,
        RecordingSource source) =>
        LivePlaylistComposer.BuildAsync(
            previous, files, MinTarget, source, CancellationToken.None);

    private static PartFileObservation Observe(int part, string content, long ticks = 1) =>
        new(part, SourcePath(part), Encoding.UTF8.GetByteCount(content), ticks);

    private static PartFileObservation Observe(int part, long length, long ticks) =>
        new(part, SourcePath(part), length, ticks);

    private static string SourcePath(int part) => $"/session/p{part:D3}.m3u8";

    private static string Expected(params (int Part, string Content)[] parts) =>
        PlaylistStitcher.Stitch(
            parts.Select(part => new PartPlaylist(part.Part, part.Content)).ToArray(),
            MinTarget,
            final: false);

    private static string Append(string playlist, params string[][] segments)
    {
        var builder = new StringBuilder(playlist);
        foreach (string[] segment in segments)
            builder.AppendJoin('\n', segment).Append('\n');
        return builder.ToString();
    }

    private static int Occurrences(string value, string token) =>
        SessionStoreTestContext.Occurrences(value, token);

    private sealed class RecordingSource : IPartPlaylistSource
    {
        private readonly Dictionary<string, string> _contents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Exception> _errors = new(StringComparer.Ordinal);

        public List<string> Reads { get; } = [];

        public RecordingSource Add(int part, string content)
        {
            _contents[SourcePath(part)] = content;
            return this;
        }

        public void ThrowOn(int part, Exception error) =>
            _errors[SourcePath(part)] = error;

        public void Reset() => Reads.Clear();

        public Task<string> ReadAsync(string path, CancellationToken ct)
        {
            Reads.Add(path);
            if (_errors.TryGetValue(path, out Exception? error))
                return Task.FromException<string>(error);

            return Task.FromResult(_contents[path]);
        }
    }
}
