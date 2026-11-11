using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

public sealed class RecordingLibraryLoadTests
{
    [Fact]
    public async Task Load_skips_dot_folders_missing_or_corrupt_manifests_and_unparseable_parts()
    {
        using var context = new SessionStoreTestContext();

        // Complete session with a metadata change, a thumbnail, and known media size.
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 1.5),
            PlaylistTestData.Segment("p001_00002.m4s", "2026-11-10T16:35:01.5Z", 2.5));
        context.WriteSegment(1, 1, 10);
        context.WriteSegment(1, 2, 20);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        await context.Store.AppendMetadataAsync(
            context.Session, context.Metadata("Changed", "New game"), CancellationToken.None);
        await File.WriteAllBytesAsync(context.ThumbnailPath, new byte[5]);
        context.Clock.Advance(TimeSpan.FromMinutes(1));
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);

        // A second, still-recording session.
        var recordingLive = new LiveStream(
            "222222222", "Recording", "Game", context.Session.StartedAt.AddHours(1), null);
        var recording = Session.For(recordingLive, "streamer", context.Root);
        await context.Store.OpenAsync(recording, recordingLive, CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(recording.Directory, "p001.m3u8"),
            PlaylistTestData.Playlist(
                6,
                "init_001.mp4",
                PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6)),
            CancellationToken.None);

        // Dot-prefixed channel and session.
        var hiddenLive = new LiveStream(
            "333333333", "Hidden", "Game", context.Session.StartedAt.AddHours(2), null);
        var hiddenChannel = Session.For(hiddenLive, ".hidden", context.Root);
        await context.Store.OpenAsync(hiddenChannel, hiddenLive, CancellationToken.None);
        var hiddenSession = new Session(
            "hidden",
            "streamer",
            "444444444",
            context.Session.StartedAt,
            Path.Combine(context.Root, "streamer", ".hidden-session"));
        await context.Store.OpenAsync(hiddenSession, hiddenLive, CancellationToken.None);

        // A folder without a manifest, a corrupt manifest, and a malformed part playlist.
        string noManifest = Path.Combine(context.Root, "streamer", "no-manifest");
        Directory.CreateDirectory(noManifest);
        await File.WriteAllTextAsync(
            Path.Combine(noManifest, "p001.m3u8"), "garbage", CancellationToken.None);

        string corrupt = Path.Combine(context.Root, "streamer", "corrupt");
        Directory.CreateDirectory(corrupt);
        await File.WriteAllTextAsync(
            Path.Combine(corrupt, "session.json"), "{ not json", CancellationToken.None);

        var brokenLive = new LiveStream(
            "555555555", "Broken", "Game", context.Session.StartedAt.AddHours(3), null);
        var broken = Session.For(brokenLive, "streamer", context.Root);
        await context.Store.OpenAsync(broken, brokenLive, CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(broken.Directory, "p001.m3u8"),
            "#EXTM3U\n#EXTINF:oops,\np001_00001.m4s\n",
            CancellationToken.None);

        // A Complete manifest with neither an index nor parts is not servable.
        var emptyLive = new LiveStream(
            "666666666", "Empty", "Game", context.Session.StartedAt.AddHours(4), null);
        var empty = Session.For(emptyLive, "streamer", context.Root);
        await context.Store.OpenAsync(empty, emptyLive, CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(empty.Directory, "p001.m3u8"),
            PlaylistTestData.Playlist(
                6,
                "init_001.mp4",
                PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6)),
            CancellationToken.None);
        await context.Store.FinalizeAsync(empty, CancellationToken.None);
        File.Delete(Path.Combine(empty.Directory, "p001.m3u8"));
        File.Delete(Path.Combine(empty.Directory, "init_001.mp4"));
        File.Delete(Path.Combine(empty.Directory, "index.m3u8"));

        var target = new RecordingLibrary(NullLogger<RecordingLibrary>.Instance);
        int changed = 0;
        target.Changed += () => changed++;
        var loader = new SessionStore(
            Options.Create(new RecorderOptions { SegmentSeconds = 6 }),
            target,
            context.Clock,
            NullLogger<SessionStore>.Instance);

        await target.LoadAsync(context.Root, loader, CancellationToken.None);

        Assert.True(target.IsLoaded);
        Assert.Equal(1, changed);
        Assert.Equal(
            [recording.Id, context.Session.Id],
            target.All.Select(summary => summary.Id));

        var complete = target.Find(context.Session.Id);
        Assert.NotNull(complete);
        Assert.False(complete.IsRecording);
        Assert.Equal("Changed", complete.Title);
        Assert.Equal("New game", complete.Game);
        Assert.Equal(1, complete.PartCount);
        Assert.Equal(2, complete.SegmentCount);
        Assert.Equal(4.0, complete.DurationSeconds);
        Assert.NotNull(complete.EndedAt);
        Assert.True(complete.HasThumbnail);
        Assert.Equal(
            3 + 10 + 20 + new FileInfo(context.IndexPath).Length,
            complete.SizeBytes);
        var chapter = Assert.Single(complete.Chapters);
        Assert.Equal(0, chapter.StartSeconds);
        Assert.Equal("Changed", chapter.Title);

        var live = target.Find(recording.Id);
        Assert.NotNull(live);
        Assert.True(live.IsRecording);
        Assert.Null(live.DurationSeconds);
        Assert.Equal(1, live.PartCount);
        Assert.Equal(1, live.SegmentCount);

        Assert.Null(target.Find(hiddenChannel.Id));
        Assert.Null(target.Find(hiddenSession.Id));
        Assert.Null(target.Find(broken.Id));
        Assert.Null(target.Find(empty.Id));
    }
}
