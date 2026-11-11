using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Hosting;
using StreamKeepr.Tests.Storage;

namespace StreamKeepr.Tests.Runtime;

public sealed class StartupRecoveryHostTests
{
    [Fact]
    public async Task Startup_recovery_loads_the_library_and_queues_SessionRecovered()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.WritePart(1);

        using var factory = new StreamKeeprAppFactory(
            new Dictionary<string, string?>
            {
                [$"{TwitchOptions.SectionName}:Channels:0"] = "streamer",
            },
            recordingsRoot: context.Root);
        using var client = factory.CreateClient();

        DvrStatus status = factory.Services.GetRequiredService<DvrStatus>();
        DvrSnapshot snapshot = await RuntimeTestWait.ForStatusAsync(
            status,
            "streamer",
            current => current.State is DvrState.Idle { Resume: not null });

        var idle = Assert.IsType<DvrState.Idle>(snapshot.State);
        Assert.Equal(context.Session.Id, idle.Resume!.Session.Id);
        Assert.Equal(2, idle.Resume.NextPart);
        Assert.True(factory.Services.GetRequiredService<RecordingLibrary>().IsLoaded);
    }

    [Fact]
    public async Task Recovery_failure_stops_host_startup()
    {
        using var context = new SessionStoreTestContext();
        await SeedAsync(context, context.Session, context.Live);

        var newerLive = new LiveStream(
            "987654322",
            "Newer",
            "Just Chatting",
            SessionStoreTestContext.StartedAt.AddHours(1),
            ThumbnailUrl: null);
        Session newer = Session.For(newerLive, "streamer", context.Root);
        await SeedAsync(context, newer, newerLive);

        // Make the older session's finalize fail; recovery must not start a partial host.
        Directory.CreateDirectory(
            Path.Combine(context.Session.Directory, SessionStore.IndexFileName + ".tmp"));

        using var factory = new StreamKeeprAppFactory(
            new Dictionary<string, string?>
            {
                [$"{TwitchOptions.SectionName}:Channels:0"] = "streamer",
            },
            recordingsRoot: context.Root);

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void Corrupt_channel_list_stops_host_startup()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            Directory.CreateDirectory(AppDataPaths.AppDataDirectory(root));
            File.WriteAllText(AppDataPaths.ChannelsFile(root), "not json");

            using var factory = new StreamKeeprAppFactory(recordingsRoot: root);
            Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Startup_starts_one_coordinator_per_stored_channel_including_paused()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var store = new ChannelStore(
                Options.Create(new TwitchOptions()),
                Options.Create(new StorageOptions { RecordingsRoot = root }),
                NullLogger<ChannelStore>.Instance);
            await store.LoadAsync(CancellationToken.None);
            await store.AddAsync("stored", CancellationToken.None);
            await store.AddAsync("paused", CancellationToken.None);
            await store.SetPausedAsync("paused", paused: true, CancellationToken.None);

            using var factory = new StreamKeeprAppFactory(recordingsRoot: root);
            using var client = factory.CreateClient();

            DvrStatus status = factory.Services.GetRequiredService<DvrStatus>();
            await RuntimeTestWait.ForStatusAsync(status, "stored", _ => true);
            DvrSnapshot paused = await RuntimeTestWait.ForStatusAsync(
                status, "paused", _ => true);

            Assert.IsType<DvrState.Paused>(paused.State);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedAsync(
        SessionStoreTestContext context, Session session, LiveStream live)
    {
        await context.Store.OpenAsync(session, live, CancellationToken.None);
        Directory.CreateDirectory(session.Directory);
        File.WriteAllText(
            Path.Combine(session.Directory, "p001.m3u8"),
            "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:6\n"
            + "#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:EVENT\n"
            + "#EXT-X-INDEPENDENT-SEGMENTS\n"
            + "#EXT-X-MAP:URI=\"init_001.mp4\"\n#EXTINF:6.0,\n"
            + "p001_00000.m4s\n");
        File.WriteAllBytes(Path.Combine(session.Directory, "init_001.mp4"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(session.Directory, "p001_00000.m4s"), [4, 5, 6]);
    }
}
