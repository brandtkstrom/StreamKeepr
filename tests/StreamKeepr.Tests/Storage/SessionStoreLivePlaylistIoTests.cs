using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;
using static StreamKeepr.Tests.Storage.PlaylistTestData;

namespace StreamKeepr.Tests.Storage;

public sealed class SessionStoreLivePlaylistIoTests
{
    [Fact]
    public async Task A_warm_refresh_reads_no_part_content()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1,
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAsync(context);
        var source = new RecordingSource();
        SessionStoreLivePlaylistIo io = NewIo(context, source);
        RecordingSummary summary = Summary(context);

        LivePlaylistRead first = await io.ReadAsync(summary, CancellationToken.None);
        Assert.Equal(LivePlaylistKind.Live, first.Kind);
        Assert.Equal([context.PartPath(1)], source.Reads);

        source.Reset();
        LivePlaylistRead second = await io.ReadAsync(summary, CancellationToken.None);

        Assert.Equal(first.Body, second.Body);
        Assert.Empty(source.Reads);
    }

    [Fact]
    public async Task An_append_only_refresh_reads_only_the_changed_last_part()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1,
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        context.WriteLivePart(2,
            Segment("p002_00001.m4s", "2026-11-10T18:30:06Z", 6));
        await OpenAsync(context);
        var source = new RecordingSource();
        SessionStoreLivePlaylistIo io = NewIo(context, source);
        RecordingSummary summary = Summary(context);

        await io.ReadAsync(summary, CancellationToken.None);
        source.Reset();

        context.WriteLivePart(2,
            Segment("p002_00001.m4s", "2026-11-10T18:30:06Z", 6),
            Segment("p002_00002.m4s", "2026-11-10T18:30:12Z", 6));

        LivePlaylistRead read = await io.ReadAsync(summary, CancellationToken.None);

        Assert.Equal([context.PartPath(2)], source.Reads);
        Assert.Equal(await FullStitchAsync(context), read.Body);
        Assert.Contains("p002_00002.m4s", read.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_part_adds_one_discontinuity_and_reads_only_the_new_part()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1,
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAsync(context);
        var source = new RecordingSource();
        SessionStoreLivePlaylistIo io = NewIo(context, source);
        RecordingSummary summary = Summary(context);

        await io.ReadAsync(summary, CancellationToken.None);
        source.Reset();

        context.WriteLivePart(2,
            Segment("p002_00001.m4s", "2026-11-10T18:30:06Z", 6));
        LivePlaylistRead read = await io.ReadAsync(summary, CancellationToken.None);

        Assert.Equal([context.PartPath(2)], source.Reads);
        Assert.Equal(await FullStitchAsync(context), read.Body);
        Assert.NotNull(read.Body);
        Assert.Equal(1, SessionStoreTestContext.Occurrences(read.Body, "#EXT-X-DISCONTINUITY"));
        Assert.True(
            read.Body.IndexOf("#EXT-X-DISCONTINUITY", StringComparison.Ordinal)
            < read.Body.IndexOf("#EXT-X-MAP:URI=\"init_002.mp4\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_temp_file_is_never_read_or_ignored_as_a_part()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1,
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAsync(context);
        var source = new RecordingSource();
        SessionStoreLivePlaylistIo io = NewIo(context, source);
        RecordingSummary summary = Summary(context);

        LivePlaylistRead first = await io.ReadAsync(summary, CancellationToken.None);

        await File.WriteAllTextAsync(
            context.PartPath(1) + ".tmp",
            LivePlaylist(20, "init_001.mp4",
                Segment("p001_99999.m4s", "2026-11-10T18:30:00Z", 6)));
        source.Reset();

        LivePlaylistRead second = await io.ReadAsync(summary, CancellationToken.None);

        Assert.Empty(source.Reads);
        Assert.Equal(first.Body, second.Body);
        Assert.DoesNotContain("p001_99999.m4s", second.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finalize_reopen_and_delete_follow_the_manifest()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1,
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAsync(context);
        var source = new RecordingSource();
        SessionStoreLivePlaylistIo io = NewIo(context, source);
        RecordingSummary summary = Summary(context);
        await io.ReadAsync(summary, CancellationToken.None);

        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        LivePlaylistRead complete = await io.ReadAsync(summary, CancellationToken.None);
        Assert.Equal(LivePlaylistKind.Complete, complete.Kind);
        Assert.Equal(await File.ReadAllTextAsync(context.IndexPath), complete.Body);
        Assert.Contains("#EXT-X-ENDLIST", complete.Body, StringComparison.Ordinal);

        // A Complete session without an index has no body.
        File.Delete(context.IndexPath);
        LivePlaylistRead missingIndex = await io.ReadAsync(summary, CancellationToken.None);
        Assert.Equal(LivePlaylistKind.Missing, missingIndex.Kind);

        // Reopen drops the Complete state and the next read rebuilds from the parts.
        await context.Store.ReopenAsync(context.Session, CancellationToken.None);
        source.Reset();
        LivePlaylistRead live = await io.ReadAsync(summary, CancellationToken.None);
        Assert.Equal(LivePlaylistKind.Live, live.Kind);
        Assert.DoesNotContain("#EXT-X-ENDLIST", live.Body, StringComparison.Ordinal);
        Assert.Equal([context.PartPath(1)], source.Reads);

        // Delete (the directory disappears) is Missing, never an exception.
        Directory.Delete(context.Directory, recursive: true);
        LivePlaylistRead deleted = await io.ReadAsync(summary, CancellationToken.None);
        Assert.Equal(LivePlaylistKind.Missing, deleted.Kind);
    }

    [Fact]
    public async Task An_unreadable_manifest_throws_so_the_service_keeps_the_last_good_body()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1,
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAsync(context);
        var io = NewIo(context, new RecordingSource());
        RecordingSummary summary = Summary(context);
        await io.ReadAsync(summary, CancellationToken.None);

        await File.WriteAllTextAsync(context.ManifestPath, "{ not json");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => io.ReadAsync(summary, CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_manifest_is_missing()
    {
        using var context = new SessionStoreTestContext();
        context.WriteLivePart(1,
            Segment("p001_00001.m4s", "2026-11-10T18:30:00Z", 6));
        await OpenAsync(context);
        var io = NewIo(context, new RecordingSource());
        RecordingSummary summary = Summary(context);

        File.Delete(context.ManifestPath);

        LivePlaylistRead read = await io.ReadAsync(summary, CancellationToken.None);
        Assert.Equal(LivePlaylistKind.Missing, read.Kind);
    }

    private static async Task OpenAsync(SessionStoreTestContext context)
    {
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();
    }

    private static SessionStoreLivePlaylistIo NewIo(
        SessionStoreTestContext context, IPartPlaylistSource source) =>
        new(
            context.Store,
            Options.Create(new StorageOptions { RecordingsRoot = context.Root }),
            source);

    private static RecordingSummary Summary(SessionStoreTestContext context) =>
        Assert.IsType<RecordingSummary>(context.Library.Find(context.Session.Id));

    private static async Task<string> FullStitchAsync(SessionStoreTestContext context)
    {
        var files = context.Store.PartPlaylistFiles(context.Directory);
        var parts = new List<PartPlaylist>(files.Count);
        foreach ((int part, string path) in files)
            parts.Add(new PartPlaylist(part, await File.ReadAllTextAsync(path)));
        return PlaylistStitcher.Stitch(parts, context.Store.MinTargetDuration, final: false);
    }

    private sealed class RecordingSource : IPartPlaylistSource
    {
        public List<string> Reads { get; } = [];

        public void Reset() => Reads.Clear();

        public Task<string> ReadAsync(string path, CancellationToken ct)
        {
            Reads.Add(path);
            return PartPlaylistReader.ReadAllTextAsync(path, ct);
        }
    }
}
