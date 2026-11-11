using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Hosting;
using StreamKeepr.Tests.Storage;

namespace StreamKeepr.Tests.Runtime;

public sealed class RestartContinuityTests
{
    private const string Channel = "streamer";

    private static readonly DateTimeOffset StartedAt =
        new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

    private static readonly LiveStream Live = new(
        "987654321", "Restart stream", "Just Chatting", StartedAt, ThumbnailUrl: null);

    [Fact]
    public async Task Graceful_stop_and_restart_with_the_stream_live_continues_as_the_next_part()
    {
        string ffmpeg = RequireFfmpeg();
        string root = NewRoot();

        try
        {
            string sessionId;
            Dictionary<string, byte[]> partOne;

            {
                var poller = new ScriptedStreamPoller();
                using var factory = CreateFactory(root, poller, ffmpeg);
                using var client = factory.CreateClient();

                await poller.Started.WaitAsync(TimeSpan.FromSeconds(10));
                DvrStatus status = factory.Services.GetRequiredService<DvrStatus>();
                poller.Send(Channel, new StreamObservation.Live(Live));

                DvrState.Recording recording = await RuntimeTestWait.ForValueAsync(
                    () => status.Current.TryGetValue(Channel, out DvrSnapshot? snapshot)
                        && snapshot.State is DvrState.Recording current
                        ? current
                        : null,
                    TimeSpan.FromSeconds(30));

                sessionId = recording.Session.Id;
                string directory = Path.Combine(root, Channel, sessionId);
                await RuntimeTestWait.UntilAsync(
                    () => File.Exists(Path.Combine(directory, "p001.m3u8"))
                        && Directory.GetFiles(directory, "p001_*.m4s").Length > 0,
                    TimeSpan.FromSeconds(30));
            }

            string sessionDirectory = Path.Combine(root, Channel, sessionId);
            partOne = CapturePart(sessionDirectory, 1);
            string partPlaylist = await File.ReadAllTextAsync(
                Path.Combine(sessionDirectory, "p001.m3u8"));
            Assert.Contains("#EXT-X-ENDLIST", partPlaylist, StringComparison.Ordinal);

            SessionManifest? manifest = await ReadManifestAsync(sessionDirectory);
            Assert.NotNull(manifest);
            Assert.Equal(SessionStatus.Recording, manifest!.Status);

            var secondPoller = new ScriptedStreamPoller();
            using (var factory = CreateFactory(root, secondPoller, ffmpeg))
            using (var client = factory.CreateClient())
            {
                await secondPoller.Started.WaitAsync(TimeSpan.FromSeconds(10));
                DvrStatus status = factory.Services.GetRequiredService<DvrStatus>();
                secondPoller.Send(Channel, new StreamObservation.Live(Live));

                DvrState.Recording recording = await RuntimeTestWait.ForValueAsync(
                    () => status.Current.TryGetValue(Channel, out DvrSnapshot? snapshot)
                        && snapshot.State is DvrState.Recording current
                        && current.Job.Part == 2
                        && current.Session.Id == sessionId
                        ? current
                        : null,
                    TimeSpan.FromSeconds(30));

                Assert.Equal(2, recording.Job.Part);
                await RuntimeTestWait.UntilAsync(
                    () => File.Exists(Path.Combine(sessionDirectory, "p002.m3u8")),
                    TimeSpan.FromSeconds(30));

                foreach ((string name, byte[] bytes) in partOne)
                {
                    Assert.Equal(bytes, await File.ReadAllBytesAsync(
                        Path.Combine(sessionDirectory, name)));
                }
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Restart_after_an_unclean_stop_cleans_leftovers_and_resumes()
    {
        using var context = new SessionStoreTestContext();
        await SeedRecoverableSessionAsync(context);

        string orphanSegment = Path.Combine(context.Directory, "p001_99999.m4s");
        string orphanInit = Path.Combine(context.Directory, "init_999.mp4");
        string temp = Path.Combine(context.Directory, "p001.m3u8.tmp");
        await File.WriteAllBytesAsync(orphanSegment, [9, 9]);
        await File.WriteAllBytesAsync(orphanInit, [9, 9]);
        await File.WriteAllTextAsync(temp, "stale");

        string ffmpeg = RequireFfmpeg();
        var poller = new ScriptedStreamPoller();
        using var factory = CreateFactory(context.Root, poller, ffmpeg);
        using var client = factory.CreateClient();

        await poller.Started.WaitAsync(TimeSpan.FromSeconds(10));
        DvrStatus status = factory.Services.GetRequiredService<DvrStatus>();
        poller.Send(Channel, new StreamObservation.Live(context.Live));

        DvrState.Recording recording = await RuntimeTestWait.ForValueAsync(
            () => status.Current.TryGetValue(Channel, out DvrSnapshot? snapshot)
                && snapshot.State is DvrState.Recording current
                && current.Session.Id == context.Session.Id
                && current.Job.Part == 2
                ? current
                : null,
            TimeSpan.FromSeconds(30));

        Assert.Equal(2, recording.Job.Part);
        await RuntimeTestWait.UntilAsync(
            () => File.Exists(Path.Combine(context.Directory, "p002.m3u8")),
            TimeSpan.FromSeconds(30));

        Assert.False(File.Exists(orphanSegment));
        Assert.False(File.Exists(orphanInit));
        Assert.False(File.Exists(temp));
        Assert.True(File.Exists(Path.Combine(context.Directory, "p001_00000.m4s")));

        using HttpResponseMessage indexResponse = await client.GetAsync(
            $"/media/{Channel}/{context.Session.Id}/index.m3u8");
        Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);
        string index = await indexResponse.Content.ReadAsStringAsync();
        Assert.Contains("p001_00000.m4s", index, StringComparison.Ordinal);

        using HttpResponseMessage segmentResponse = await client.GetAsync(
            $"/media/{Channel}/{context.Session.Id}/p001_00000.m4s");
        Assert.Equal(HttpStatusCode.OK, segmentResponse.StatusCode);
        Assert.NotEmpty(await segmentResponse.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Stream_that_ended_during_downtime_is_finalized_and_playable_on_restart()
    {
        using var context = new SessionStoreTestContext();
        await SeedRecoverableSessionAsync(context);

        string ffmpeg = RequireFfmpeg();
        var poller = new ScriptedStreamPoller();
        using var factory = CreateFactory(context.Root, poller, ffmpeg);
        using var client = factory.CreateClient();

        await poller.Started.WaitAsync(TimeSpan.FromSeconds(10));
        RecordingLibrary library = factory.Services.GetRequiredService<RecordingLibrary>();
        poller.Send(Channel, new StreamObservation.Offline());

        RecordingSummary summary = await RuntimeTestWait.ForValueAsync(
            () => library.Find(context.Session.Id) is { IsRecording: false } found
                ? found
                : null,
            TimeSpan.FromSeconds(30));

        using HttpResponseMessage indexResponse =
            await client.GetAsync($"/media/{Channel}/{summary.Id}/index.m3u8");

        Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);
        Assert.Equal(
            RecordingsMedia.PlaylistContentType,
            indexResponse.Content.Headers.ContentType?.MediaType);

        string index = await indexResponse.Content.ReadAsStringAsync();
        Assert.Contains("#EXT-X-ENDLIST", index, StringComparison.Ordinal);

        string segment = index
            .Split('\n')
            .Select(line => line.Trim())
            .First(line => line.Length > 0
                && line[0] != '#'
                && line.EndsWith(".m4s", StringComparison.Ordinal));

        using HttpResponseMessage segmentResponse =
            await client.GetAsync($"/media/{Channel}/{summary.Id}/{segment}");

        Assert.Equal(HttpStatusCode.OK, segmentResponse.StatusCode);
        Assert.NotEmpty(await segmentResponse.Content.ReadAsByteArrayAsync());
    }

    private static async Task SeedRecoverableSessionAsync(SessionStoreTestContext context)
    {
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00000.m4s", "2026-11-10T18:30:00Z", 6));
        context.WriteSegment(1, 0);

        // A hard kill leaves the part playlist open, without ENDLIST.
        string playlistPath = Path.Combine(context.Directory, "p001.m3u8");
        string playlist = await File.ReadAllTextAsync(playlistPath);
        await File.WriteAllTextAsync(
            playlistPath, playlist.Replace("#EXT-X-ENDLIST", "", StringComparison.Ordinal));
    }

    private static StreamKeeprAppFactory CreateFactory(
        string root, ScriptedStreamPoller poller, string ffmpeg)
    {
        var overrides = new Dictionary<string, string?>
        {
            [$"{TwitchOptions.SectionName}:Channels:0"] = Channel,
            [$"{RecorderOptions.SectionName}:StreamlinkPath"] =
                TestPaths.FixturePath("fake-live-streamlink"),
            [$"{RecorderOptions.SectionName}:FfmpegPath"] = ffmpeg,
            [$"{RecorderOptions.SectionName}:SegmentSeconds"] = "2",
            [$"{RecorderOptions.SectionName}:FinalizeTimeoutSeconds"] = "5",
            [$"{RecorderOptions.SectionName}:StallTimeoutSeconds"] = "30",
            [$"{RecorderOptions.SectionName}:RetryBaseSeconds"] = "60",
            [$"{RecorderOptions.SectionName}:RetryMaxSeconds"] = "600",
            [$"{StorageOptions.SectionName}:MinFreeGb"] = "1",
        };

        return new StreamKeeprAppFactory(
            overrides,
            recordingsRoot: root,
            configureTestServices: services => services.AddSingleton<IStreamPoller>(poller));
    }

    private static Dictionary<string, byte[]> CapturePart(string directory, int part)
    {
        string playlist = $"p{part:D3}";
        string init = $"init_{part:D3}";

        return Directory.EnumerateFiles(directory)
            .Where(file =>
            {
                string name = Path.GetFileName(file);
                return name.StartsWith(playlist, StringComparison.Ordinal)
                    || name.StartsWith(init, StringComparison.Ordinal);
            })
            .ToDictionary(
                file => Path.GetFileName(file),
                File.ReadAllBytes,
                StringComparer.Ordinal);
    }

    private static async Task<SessionManifest?> ReadManifestAsync(string sessionDirectory)
    {
        var store = new SessionStore(
            Options.Create(new RecorderOptions { SegmentSeconds = 2 }),
            new RecordingLibrary(NullLogger<RecordingLibrary>.Instance),
            TimeProvider.System,
            NullLogger<SessionStore>.Instance);

        return await store.TryLoadAsync(sessionDirectory, CancellationToken.None);
    }

    private static string RequireFfmpeg() => TestPaths.RequireExecutable(
        "ffmpeg",
        "ffmpeg is required for restart-continuity tests. Install ffmpeg and put it on PATH.");

    private static string NewRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

}
