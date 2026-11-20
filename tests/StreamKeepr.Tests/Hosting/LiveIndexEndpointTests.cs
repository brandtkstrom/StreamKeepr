using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Storage;

namespace StreamKeepr.Tests.Hosting;

public sealed class LiveIndexEndpointTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Recording_session_serves_the_cached_snapshot_after_the_directory_is_deleted()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "123456789");
        string body = LiveBody("p001_00001.m4s");
        LivePlaylistSnapshot snapshot = Publish(factory, session.Id, body);
        Directory.Delete(session.Directory, recursive: true);

        using var get = await client.GetAsync(
            $"/media/{session.Channel}/{session.Id}/index.m3u8");

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(RecordingsMedia.PlaylistContentType, get.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", get.Headers.CacheControl?.ToString());
        Assert.Equal(snapshot.Utf8.Length, get.Content.Headers.ContentLength);
        Assert.Equal(body, await get.Content.ReadAsStringAsync());
        Assert.Equal(snapshot.Utf8.ToArray(), await get.Content.ReadAsByteArrayAsync());
        Assert.DoesNotContain('\r', body);
        Assert.DoesNotContain("#EXT-X-ENDLIST", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serving_a_snapshot_does_not_wait_for_a_blocked_refresh()
    {
        var io = new BlockingLivePlaylistIo();
        using var factory = CreateFactory(services =>
            services.AddSingleton<ILivePlaylistIo>(io));
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "923456789");
        string body = LiveBody("p001_00001.m4s");
        Publish(factory, session.Id, body);
        io.Block = true;
        var summary = Assert.IsType<RecordingSummary>(
            factory.Services.GetRequiredService<RecordingLibrary>().Find(session.Id));
        LivePlaylistService live = factory.Services.GetRequiredService<LivePlaylistService>();

        Task refresh = live.RefreshOneAsync(summary, CancellationToken.None);
        await io.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var response = await client
            .GetAsync($"/media/{session.Channel}/{session.Id}/index.m3u8")
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());

        io.Release.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Concurrent_requests_serve_one_whole_snapshot_while_a_refresh_is_blocked()
    {
        var io = new BlockingLivePlaylistIo();
        using var factory = CreateFactory(services =>
            services.AddSingleton<ILivePlaylistIo>(io));
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "933456789");
        string body = LiveBody("p001_00001.m4s");
        Publish(factory, session.Id, body);
        string url = $"/media/{session.Channel}/{session.Id}/index.m3u8";
        io.Block = true;
        var summary = Assert.IsType<RecordingSummary>(
            factory.Services.GetRequiredService<RecordingLibrary>().Find(session.Id));
        LivePlaylistService live = factory.Services.GetRequiredService<LivePlaylistService>();
        Task refresh = live.RefreshOneAsync(summary, CancellationToken.None);
        await io.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(_ => client.GetAsync(url)));
        try
        {
            Assert.All(responses, response =>
                Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            foreach (HttpResponseMessage response in responses)
                Assert.Equal(body, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
                response.Dispose();
            io.Release.SetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Cold_live_cache_returns_503_with_retry_after()
    {
        using var factory = CreateFactory(services =>
            services.AddSingleton<ILivePlaylistIo>(
                new MissingLivePlaylistIo()));
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "223456789");

        using var response = await client.GetAsync(
            $"/media/{session.Channel}/{session.Id}/index.m3u8");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("1", response.Headers.RetryAfter?.ToString());
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Unloaded_library_returns_503_with_retry_after()
    {
        var library = new RecordingLibrary(
            TimeProvider.System, NullLogger<RecordingLibrary>.Instance);
        var context = new DefaultHttpContext();

        IResult result = await LiveIndexEndpoint.HandleAsync(
            "streamer",
            "20260101T000000Z_000000001",
            context,
            library,
            new LivePlaylistCache(),
            Options.Create(new StorageOptions { RecordingsRoot = Path.GetTempPath() }));
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("1", context.Response.Headers.RetryAfter);
        Assert.Equal("no-cache", context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task Head_matches_get_content_length_without_a_body()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "323456789");
        string body = LiveBody("p001_00001.m4s");
        LivePlaylistSnapshot snapshot = Publish(factory, session.Id, body);
        string url = $"/media/{session.Channel}/{session.Id}/index.m3u8";

        using var get = await client.GetAsync(url);
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
        using var head = await client.SendAsync(headRequest);

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
        Assert.Equal(snapshot.Utf8.Length, head.Content.Headers.ContentLength);
        Assert.Equal("no-cache", head.Headers.CacheControl?.ToString());
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Client_abort_is_not_an_error()
    {
        using var factory = CreateFactory(services =>
            services.AddSingleton<IStartupFilter, AbortingBodyStartupFilter>());
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "423456789");
        Publish(factory, session.Id, LiveBody("p001_00001.m4s"));

        using var response = await client.GetAsync(
            $"/media/{session.Channel}/{session.Id}/index.m3u8");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task An_aborted_write_with_an_ioexception_is_not_an_error()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.RequestAborted = new CancellationToken(canceled: true);
        context.Response.Body = new AbortingStream(ioException: true);
        LivePlaylistSnapshot snapshot = LivePlaylistSnapshot.Create(
            "20260101T000000Z_000000001",
            1,
            LiveBody("p001_00001.m4s"),
            1,
            StartedAt);

        await new LivePlaylistResult(snapshot).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(snapshot.Utf8.Length, context.Response.ContentLength);
    }

    [Fact]
    public async Task Complete_session_returns_the_finalized_index_with_one_endlist()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "523456789");
        await WritePartAsync(session, "p001_00001.m4s");
        await factory.Services.GetRequiredService<SessionStore>()
            .FinalizeAsync(session, CancellationToken.None);

        using var response = await client.GetAsync(
            $"/media/{session.Channel}/{session.Id}/index.m3u8");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RecordingsMedia.PlaylistContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());

        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("p001_00001.m4s", body, StringComparison.Ordinal);
        Assert.Equal(1, SessionStoreTestContext.Occurrences(body, "#EXT-X-ENDLIST"));
    }

    [Fact]
    public async Task Complete_session_supports_head_and_range_requests()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "623456789");
        await WritePartAsync(session, "p001_00001.m4s");
        await factory.Services.GetRequiredService<SessionStore>()
            .FinalizeAsync(session, CancellationToken.None);
        string url = $"/media/{session.Channel}/{session.Id}/index.m3u8";

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
        using var head = await client.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.True(head.Content.Headers.ContentLength > 0);
        Assert.Equal("no-cache", head.Headers.CacheControl?.ToString());
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 9);
        using var range = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(10, (await range.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal("no-cache", range.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Finalize_switches_to_the_index_with_endlist()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "723456789");
        await WritePartAsync(session, "p001_00001.m4s");
        Publish(factory, session.Id, LiveBody("p001_00001.m4s"));

        await factory.Services.GetRequiredService<SessionStore>()
            .FinalizeAsync(session, CancellationToken.None);

        using var response = await client.GetAsync(
            $"/media/{session.Channel}/{session.Id}/index.m3u8");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(1, SessionStoreTestContext.Occurrences(body, "#EXT-X-ENDLIST"));
        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(session.Directory, "index.m3u8")),
            body);
    }

    [Fact]
    public async Task Loaded_library_refuses_a_session_removed_from_it()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "823456789");

        var library = factory.Services.GetRequiredService<RecordingLibrary>();
        library.Remove(session.Id);

        using var response = await client.GetAsync(
            $"/media/{session.Channel}/{session.Id}/index.m3u8");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Unknown_channel_or_session_returns_404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "923456789");

        using var unknownSession = await client.GetAsync(
            $"/media/{session.Channel}/20260101T000000Z_999999999/index.m3u8");
        using var unknownChannel = await client.GetAsync(
            $"/media/missing/{session.Id}/index.m3u8");

        Assert.Equal(HttpStatusCode.NotFound, unknownSession.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownChannel.StatusCode);
        Assert.Equal("no-cache", unknownSession.Headers.CacheControl?.ToString());
        Assert.Equal("no-cache", unknownChannel.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Invalid_channel_or_session_id_returns_404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "133456789");

        using var uppercaseChannel = await client.GetAsync(
            $"/media/Streamer/{session.Id}/index.m3u8");
        using var badId = await client.GetAsync(
            $"/media/{session.Channel}/not-a-session/index.m3u8");

        Assert.Equal(HttpStatusCode.NotFound, uppercaseChannel.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, badId.StatusCode);
        Assert.Equal("no-cache", uppercaseChannel.Headers.CacheControl?.ToString());
        Assert.Equal("no-cache", badId.Headers.CacheControl?.ToString());
    }

    private static StreamKeeprAppFactory CreateFactory(
        Action<IServiceCollection>? configure = null) =>
        new(
            new Dictionary<string, string?>
            {
                [$"{LivePlaylistOptions.SectionName}:RefreshSeconds"] = "3600",
            },
            configureTestServices: configure);

    private static LivePlaylistSnapshot Publish(
        StreamKeeprAppFactory factory, string sessionId, string body)
    {
        LivePlaylistSnapshot snapshot = LivePlaylistSnapshot.Create(
            sessionId, 1, body, PlaylistStitcher.SegmentCount(body), StartedAt);
        factory.Services.GetRequiredService<LivePlaylistCache>().Publish(snapshot);
        return snapshot;
    }

    private static string LiveBody(string segment)
    {
        var part = new PartPlaylist(
            1,
            PlaylistTestData.Playlist(
                6, "init_001.mp4", PlaylistTestData.Segment(segment, "2026-11-10T16:35:00Z", 6)));
        return PlaylistStitcher.Stitch([part], 12, final: false);
    }

    private static async Task<Session> OpenSessionAsync(
        StreamKeeprAppFactory factory, string streamId)
    {
        var live = new LiveStream(streamId, "Title", "Game", StartedAt, null);
        var session = Session.For(live, "streamer", factory.RecordingsRoot);
        await factory.Services.GetRequiredService<SessionStore>()
            .OpenAsync(session, live, CancellationToken.None);
        return session;
    }

    private static Task WritePartAsync(Session session, string segment)
    {
        Directory.CreateDirectory(session.Directory);
        return File.WriteAllTextAsync(
            Path.Combine(session.Directory, "p001.m3u8"),
            PlaylistTestData.Playlist(
                6,
                "init_001.mp4",
                PlaylistTestData.Segment(segment, "2026-11-10T16:35:00Z", 6)));
    }

    private sealed class MissingLivePlaylistIo : ILivePlaylistIo
    {
        public Task<LivePlaylistRead> ReadAsync(
            RecordingSummary summary, CancellationToken ct) =>
            Task.FromResult(LivePlaylistRead.Missing);
    }

    private sealed class BlockingLivePlaylistIo : ILivePlaylistIo
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private volatile bool _block;

        public bool Block
        {
            get => _block;
            set => _block = value;
        }

        public async Task<LivePlaylistRead> ReadAsync(
            RecordingSummary summary, CancellationToken ct)
        {
            if (!_block)
                return LivePlaylistRead.Missing;

            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return LivePlaylistRead.Missing;
        }
    }

    /// <summary>
    /// Replaces the response body for the live index with a stream that aborts the write, and
    /// marks the request cancelled, so the endpoint's abort handling runs through the real
    /// pipeline and request logging.
    /// </summary>
    private sealed class AbortingBodyStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path.Value?.EndsWith(
                        "/index.m3u8", StringComparison.Ordinal) == true)
                {
                    context.RequestAborted = new CancellationToken(canceled: true);
                    context.Response.Body = new AbortingStream();
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class AbortingStream(bool ioException = false) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw Abort();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(Abort());

        private Exception Abort() => ioException
            ? new IOException("The client closed the connection.")
            : new OperationCanceledException();
    }
}
