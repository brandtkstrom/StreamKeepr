using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Storage;

public sealed class SessionStoreConcurrencyTests
{
    [Fact]
    public async Task Concurrent_finalize_append_and_summarize_do_not_corrupt_the_session()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        context.WritePart(
            2,
            PlaylistTestData.Segment("p002_00001.m4s", "2026-11-10T16:35:06Z", 6));
        context.WriteSegment(1, 1, 10);
        context.WriteSegment(2, 1, 20);
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        var operations = new List<Task>(36)
        {
            context.Store.FinalizeAsync(context.Session, CancellationToken.None),
        };

        for (int i = 0; i < 25; i++)
        {
            int title = i;
            operations.Add(context.Store.AppendMetadataAsync(
                context.Session,
                context.Metadata($"Title {title}", $"Game {title}"),
                CancellationToken.None));
        }

        for (int i = 0; i < 10; i++)
            operations.Add(context.Store.SummarizeAsync(context.Directory, CancellationToken.None));

        await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(15));

        var manifest = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Equal(SessionStatus.Complete, manifest.Status);
        Assert.NotEmpty(manifest.Metadata);
        Assert.All(manifest.Metadata, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Title));
            Assert.False(string.IsNullOrWhiteSpace(entry.Game));
        });

        string index = await File.ReadAllTextAsync(context.IndexPath);
        Assert.Equal(1, SessionStoreTestContext.Occurrences(index, "#EXT-X-ENDLIST"));
        Assert.Equal(1, SessionStoreTestContext.Occurrences(index, "#EXT-X-DISCONTINUITY"));
        Assert.Equal(12.0, PlaylistTimeline.Parse(index).Duration);

        Assert.False(File.Exists(context.ManifestPath + ".tmp"));
        Assert.False(File.Exists(context.IndexPath + ".tmp"));
        Assert.Equal(1, context.Store.LockFor(context.Directory).CurrentCount);
    }

    [Fact]
    public async Task Holding_the_manifest_lock_does_not_block_a_live_refresh()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        context.WriteSegment(1, 1, 10);
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();
        var cache = new LivePlaylistCache();
        LivePlaylistService service = NewService(context, cache);

        var gate = context.Store.LockFor(context.Directory);
        await gate.WaitAsync();
        try
        {
            RecordingSummary summary = RecordingSummary(context);
            string? body = await service.RefreshOneAsync(summary, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.NotNull(body);
            Assert.NotNull(cache.Find(summary.Id));
            Assert.DoesNotContain("#EXT-X-ENDLIST", body, StringComparison.Ordinal);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task Holding_the_refresh_gate_does_not_block_finalize()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        context.WriteSegment(1, 1, 10);
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();
        var cache = new LivePlaylistCache();
        var io = new BlockingIo();
        LivePlaylistService service = NewService(context, cache, io);
        RecordingSummary summary = RecordingSummary(context);

        Task refresh = service.RefreshOneAsync(summary, CancellationToken.None);
        await io.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // The refresh gate is held for the whole blocked read. Finalize must not wait for it.
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(Assert.IsType<RecordingSummary>(
            context.Library.Find(context.Session.Id)).IsRecording);

        io.Release.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(cache.Find(context.Session.Id));
    }

    [Fact]
    public async Task Finalize_and_refresh_do_not_deadlock()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(
            1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        context.WriteSegment(1, 1, 10);
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Library.MarkLoaded();
        var cache = new LivePlaylistCache();
        var io = new BlockingIo();
        LivePlaylistService service = NewService(context, cache, io);
        RecordingSummary summary = RecordingSummary(context);

        Task refresh = service.RefreshOneAsync(summary, CancellationToken.None);
        await io.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task finalize = context.Store.FinalizeAsync(context.Session, CancellationToken.None);

        await finalize.WaitAsync(TimeSpan.FromSeconds(2));
        io.Release.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(cache.Find(summary.Id));
        Assert.Contains("p001_00001.m4s", await File.ReadAllTextAsync(context.IndexPath));
    }

    [Fact]
    public async Task Concurrent_finalize_refresh_and_requests_never_return_500()
    {
        using var factory = new StreamKeeprAppFactory(new Dictionary<string, string?>
        {
            ["LivePlaylist:RefreshSeconds"] = "3600",
        });
        using var client = factory.CreateClient();
        var live = new LiveStream(
            "123456789", "Title", "Game", SessionStoreTestContext.StartedAt, null);
        Session session = Session.For(live, "streamer", factory.RecordingsRoot);
        Directory.CreateDirectory(session.Directory);
        await File.WriteAllTextAsync(
            Path.Combine(session.Directory, "p001.m3u8"),
            PlaylistTestData.Playlist(
                6,
                "init_001.mp4",
                PlaylistTestData.Segment(
                    "p001_00001.m4s", "2026-11-10T16:35:00Z", 6)));
        await File.WriteAllBytesAsync(
            Path.Combine(session.Directory, "p001_00001.m4s"), new byte[10]);

        SessionStore store = factory.Services.GetRequiredService<SessionStore>();
        await store.OpenAsync(session, live, CancellationToken.None);
        RecordingSummary summary = Assert.IsType<RecordingSummary>(
            factory.Services.GetRequiredService<RecordingLibrary>().Find(session.Id));
        LivePlaylistService service = factory.Services.GetRequiredService<LivePlaylistService>();
        await service.RefreshOneAsync(summary, CancellationToken.None);

        string url = $"/media/{session.Channel}/{session.Id}/index.m3u8";
        Task<HttpResponseMessage>[] requests = Enumerable.Range(0, 50)
            .Select(_ => client.GetAsync(url))
            .ToArray();
        Task[] refreshes = Enumerable.Range(0, 10)
            .Select(_ => service.RefreshOneAsync(summary, CancellationToken.None))
            .ToArray();
        Task finalize = store.FinalizeAsync(session, CancellationToken.None);

        HttpResponseMessage[] responses = await Task.WhenAll(requests)
            .WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            // A refresh that reads the manifest just after finalize can briefly prune the
            // cache before the library records Complete; the documented answer for a cold
            // cache is 503, never a 500.
            Assert.All(responses, response => Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable));
            await Task.WhenAll(refreshes).WaitAsync(TimeSpan.FromSeconds(15));
            await finalize.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
                response.Dispose();
        }

        using var final = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, final.StatusCode);
        string body = await final.Content.ReadAsStringAsync();
        Assert.Contains("p001_00001.m4s", body, StringComparison.Ordinal);
        Assert.Equal(1, SessionStoreTestContext.Occurrences(body, "#EXT-X-ENDLIST"));
    }

    [Fact]
    public async Task Atomic_write_retries_while_a_reader_still_holds_the_manifest()
    {
        FileStream? blocker = null;
        using var context = new SessionStoreTestContext(retryDelay: (_, _) =>
        {
            blocker?.Dispose();
            return Task.CompletedTask;
        });
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        // A handle without delete sharing blocks Windows' replace. The injected retry releases
        // it, proving the write survives a sharing violation instead of failing.
        blocker = new FileStream(
            context.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            await context.Store.AppendMetadataAsync(
                context.Session,
                context.Metadata("Retried title", "Retried game"),
                CancellationToken.None);
        }
        finally
        {
            blocker.Dispose();
        }

        var manifest = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Contains(manifest.Metadata, entry => entry.Title == "Retried title");
    }

    [Fact]
    public async Task Two_sessions_use_independent_locks()
    {
        using var context = new SessionStoreTestContext();
        var otherLive = new LiveStream(
            "987654321", "Other", "Other game", context.Session.StartedAt, null);
        var other = Session.For(otherLive, "other", context.Root);

        var firstGate = context.Store.LockFor(context.Session.Directory);
        var secondGate = context.Store.LockFor(other.Directory);

        Assert.NotSame(firstGate, secondGate);
        await firstGate.WaitAsync();
        try
        {
            Assert.True(await secondGate.WaitAsync(TimeSpan.FromSeconds(1)));
            secondGate.Release();
        }
        finally
        {
            firstGate.Release();
        }
    }

    private static LivePlaylistService NewService(
        SessionStoreTestContext context,
        LivePlaylistCache cache,
        ILivePlaylistIo? io = null) =>
        new(
            context.Library,
            cache,
            io ?? new SessionStoreLivePlaylistIo(
                context.Store,
                Options.Create(new StorageOptions { RecordingsRoot = context.Root })),
            Options.Create(new LivePlaylistOptions()),
            context.Clock,
            NullLogger<LivePlaylistService>.Instance);

    private static RecordingSummary RecordingSummary(SessionStoreTestContext context) =>
        Assert.IsType<RecordingSummary>(context.Library.Find(context.Session.Id));

    private sealed class BlockingIo : ILivePlaylistIo
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LivePlaylistRead> ReadAsync(
            RecordingSummary summary, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return LivePlaylistRead.Live("#EXTM3U\n#EXT-X-VERSION:7\n");
        }
    }
}
