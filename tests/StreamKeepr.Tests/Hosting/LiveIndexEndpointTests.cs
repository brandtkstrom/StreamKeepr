using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
        LiveIndexSnapshot snapshot = await PublishAsync(factory, session, "p001_00001.m4s");
        string body = snapshot.Body;
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
        var io = new ScriptedPartSource();
        using var factory = CreateFactory(services =>
            services.AddSingleton<IPartPlaylistSource>(io));
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "923456789");
        string body = (await PublishAsync(factory, session, "p001_00001.m4s")).Body;
        await WritePartAsync(session, "p001_00001.m4s", "p001_00002.m4s");
        io.BlockAll();
        var summary = Assert.IsType<RecordingSummary>(
            factory.Services.GetRequiredService<RecordingLibrary>().Find(session.Id));
        LiveIndex live = factory.Services.GetRequiredService<LiveIndex>();

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
        var io = new ScriptedPartSource();
        using var factory = CreateFactory(services =>
            services.AddSingleton<IPartPlaylistSource>(io));
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "933456789");
        string body = (await PublishAsync(factory, session, "p001_00001.m4s")).Body;
        string url = $"/media/{session.Channel}/{session.Id}/index.m3u8";
        await WritePartAsync(session, "p001_00001.m4s", "p001_00002.m4s");
        io.BlockAll();
        var summary = Assert.IsType<RecordingSummary>(
            factory.Services.GetRequiredService<RecordingLibrary>().Find(session.Id));
        LiveIndex live = factory.Services.GetRequiredService<LiveIndex>();
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
    public async Task Cold_live_index_returns_503_with_retry_after()
    {
        using var session = await LiveSessionAsync();
        LiveIndex index = LiveIndexTests.NewIndex(session);

        DefaultHttpContext context = await HandleAsync(session, index);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("1", context.Response.Headers.RetryAfter);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task Head_matches_get_content_length_without_a_body()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "323456789");
        LiveIndexSnapshot snapshot = await PublishAsync(factory, session, "p001_00001.m4s");
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
        await PublishAsync(factory, session, "p001_00001.m4s");

        using var response = await client.GetAsync(
            $"/media/{session.Channel}/{session.Id}/index.m3u8");

        Assert.Equal((HttpStatusCode)StatusCodes.Status499ClientClosedRequest, response.StatusCode);
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task An_aborted_write_with_an_ioexception_is_not_an_error()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.RequestAborted = new CancellationToken(canceled: true);
        context.Response.Body = new AbortingStream(ioException: true);
        LiveIndexSnapshot snapshot = LiveIndexSnapshot.Create(
            "20260101T000000Z_000000001", LiveBody("p001_00001.m4s"), StartedAt);

        await new LiveIndexResult(snapshot, stale: false).ExecuteAsync(context);

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
    public async Task Finalize_switches_from_live_snapshot_to_index_with_endlist()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "723456789");
        await PublishAsync(factory, session, "p001_00001.m4s");

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
    public async Task Deleted_session_is_404_immediately()
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
    public async Task Concurrent_requests_never_see_a_torn_snapshot()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "943456789");
        LiveIndex live = factory.Services.GetRequiredService<LiveIndex>();
        string bodyA = (await PublishAsync(factory, session, "p001_00001.m4s")).Body;
        string bodyB = (await PublishAsync(factory, session, "p001_00001.m4s", "p001_00002.m4s")).Body;
        RecordingSummary summary = Assert.IsType<RecordingSummary>(
            factory.Services.GetRequiredService<RecordingLibrary>().Find(session.Id));
        using var stop = new CancellationTokenSource();
        Task writer = Task.Run(async () =>
        {
            bool grown = true;
            while (!stop.IsCancellationRequested)
            {
                grown = !grown;
                if (grown)
                    await WritePartAsync(session, "p001_00001.m4s", "p001_00002.m4s");
                else
                    await WritePartAsync(session, "p001_00001.m4s");
                await live.RefreshOneAsync(summary, CancellationToken.None);
            }
        });
        try
        {
            string url = $"/media/{session.Channel}/{session.Id}/index.m3u8";
            string[] bodies = await Task.WhenAll(
                Enumerable.Range(0, 50).Select(_ => client.GetStringAsync(url)));

            Assert.All(bodies, body => Assert.True(
                body == bodyA || body == bodyB,
                "A response contained a body that was neither published version."));
        }
        finally
        {
            stop.Cancel();
            await writer;
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3600, true)]
    public async Task A_stale_live_lookup_adds_the_stale_marker_to_a_200(int ageSeconds, bool stale)
    {
        using var session = await LiveSessionAsync();
        (LiveIndex index, string body) = await PublishedIndexAsync(session);
        session.Clock.Advance(TimeSpan.FromSeconds(ageSeconds));

        DefaultHttpContext context = await HandleAsync(session, index);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("no-cache", context.Response.Headers.CacheControl);
        Assert.Equal(stale, context.Response.Headers.ContainsKey(LiveIndexEndpoint.StaleHeaderName));
        if (stale)
            Assert.Equal("true", context.Response.Headers[LiveIndexEndpoint.StaleHeaderName]);
        Assert.Equal(body, await ReadBodyAsync(context));
        Assert.DoesNotContain("#EXT-X-ENDLIST", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Head_matches_the_last_known_good_content_length()
    {
        using var session = await LiveSessionAsync();
        (LiveIndex index, _) = await PublishedIndexAsync(session);
        LiveIndexSnapshot snapshot = Assert.IsType<LiveIndexSnapshot>(index.Find(session.Session.Id));
        session.Clock.Advance(TimeSpan.FromMinutes(1));

        DefaultHttpContext context = await HandleAsync(session, index, method: HttpMethods.Head);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(snapshot.Utf8.Length, context.Response.ContentLength);
        Assert.Equal("true", context.Response.Headers[LiveIndexEndpoint.StaleHeaderName]);
        Assert.Empty(await ReadBodyBytesAsync(context));
    }

    [Fact]
    public async Task An_already_aborted_request_is_499_without_a_body_or_a_warning()
    {
        using var session = await LiveSessionAsync();
        (LiveIndex index, _) = await PublishedIndexAsync(session);
        var logs = new ListLoggerProvider();
        using ILoggerFactory loggers = TestLogging.Factory(logs);

        DefaultHttpContext context = await HandleAsync(
            session,
            index,
            requestAborted: new CancellationToken(canceled: true),
            loggers: loggers);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
        Assert.Empty(await ReadBodyBytesAsync(context));
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Contains(
            logs.Entries,
            entry => entry.Level == LogLevel.Debug
                && entry.Message.Contains(session.Session.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Three_hundred_twenty_warm_requests_never_return_a_server_error()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Session session = await OpenSessionAsync(factory, "103456789");
        await PublishAsync(factory, session, "p001_00001.m4s");
        string url = $"/media/{session.Channel}/{session.Id}/index.m3u8";

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 320).Select(_ => client.GetAsync(url)));
        try
        {
            Assert.All(responses, response => Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable,
                $"Unexpected status {(int)response.StatusCode}."));
            Assert.DoesNotContain(responses, response => (int)response.StatusCode >= 500);
            Assert.Equal(320, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
                response.Dispose();
        }
    }

    /// <summary>A Recording session with one part on disk and a loaded library.</summary>
    private static async Task<SessionStoreTestContext> LiveSessionAsync()
    {
        var session = new SessionStoreTestContext();
        session.WritePart(
            1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        await session.Store.OpenAsync(session.Session, session.Live, CancellationToken.None);
        session.Library.MarkLoaded();
        return session;
    }

    /// <summary>A live index that published the session's body at the session clock's now.</summary>
    private static async Task<(LiveIndex Index, string Body)> PublishedIndexAsync(
        SessionStoreTestContext session)
    {
        LiveIndex index = LiveIndexTests.NewIndex(session);
        RecordingSummary summary = Assert.IsType<RecordingSummary>(
            session.Library.Find(session.Session.Id));
        string? body = await index.RefreshOneAsync(summary, CancellationToken.None);
        return (index, Assert.IsType<string>(body));
    }

    private static async Task<DefaultHttpContext> HandleAsync(
        SessionStoreTestContext session,
        LiveIndex index,
        string method = "GET",
        CancellationToken requestAborted = default,
        ILoggerFactory? loggers = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.RequestAborted = requestAborted;
        context.Response.Body = new MemoryStream();

        IResult result = await LiveIndexEndpoint.HandleAsync(
            session.Session.Channel,
            session.Session.Id,
            context,
            index,
            loggers ?? NullLoggerFactory.Instance);
        await result.ExecuteAsync(context);
        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static async Task<byte[]> ReadBodyBytesAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var buffer = new MemoryStream();
        await context.Response.Body.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static StreamKeeprAppFactory CreateFactory(
        Action<IServiceCollection>? configure = null) =>
        new(
            new Dictionary<string, string?>
            {
                [$"{LivePlaylistOptions.SectionName}:RefreshSeconds"] = "3600",
            },
            configureTestServices: configure);

    /// <summary>Writes part 1 with the given segments and refreshes the live index from disk.</summary>
    private static async Task<LiveIndexSnapshot> PublishAsync(
        StreamKeeprAppFactory factory, Session session, params string[] segments)
    {
        await WritePartAsync(session, segments);
        RecordingSummary summary = Assert.IsType<RecordingSummary>(
            factory.Services.GetRequiredService<RecordingLibrary>().Find(session.Id));
        LiveIndex live = factory.Services.GetRequiredService<LiveIndex>();
        await live.RefreshOneAsync(summary, CancellationToken.None);
        return Assert.IsType<LiveIndexSnapshot>(live.Find(session.Id));
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

    private static Task WritePartAsync(Session session, params string[] segments)
    {
        Directory.CreateDirectory(session.Directory);
        return File.WriteAllTextAsync(
            Path.Combine(session.Directory, "p001.m3u8"),
            PlaylistTestData.LivePlaylist(
                6,
                "init_001.mp4",
                segments
                    .Select((segment, i) => PlaylistTestData.Segment(
                        segment, $"2026-11-10T16:35:{i * 6:00}Z", 6))
                    .ToArray()));
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
