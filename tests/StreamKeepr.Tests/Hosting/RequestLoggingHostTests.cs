using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StreamKeepr.Domain;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Storage;

namespace StreamKeepr.Tests.Hosting;

/// <summary>
/// The request-completion event through the real pipeline. Each request body is read to the
/// end before asserting: the test server completes the body only after the pipeline returns,
/// so the completion event has already been written.
/// </summary>
public sealed class RequestLoggingHostTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_page_request_logs_one_information_event_with_its_request_id()
    {
        using var factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        await GetAsync(client, "/");

        CapturedLogEntry request = Assert.Single(RequestEvents(factory));
        Assert.Equal(LogLevel.Information, request.Level);
        Assert.Equal("GET", request.Properties["RequestMethod"]);
        Assert.Equal("/", request.Properties["RequestPath"]);
        Assert.Equal(200, request.Properties["StatusCode"]);
        Assert.NotNull(request.Properties["Elapsed"]);
        string requestId = Assert.IsType<string>(request.Properties["RequestId"]);
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        Assert.Contains(requestId, request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_successful_health_probe_logs_no_request_event()
    {
        using var factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        HttpStatusCode health = await GetAsync(client, "/health/live");
        await GetAsync(client, "/");

        Assert.Equal(HttpStatusCode.OK, health);
        // The page request proves request logging is live; the probe left nothing behind.
        Assert.Equal(["/"], RequestEvents(factory).Select(request => request.Properties["RequestPath"]));
    }

    [Fact]
    public async Task A_missing_media_file_logs_a_warning()
    {
        using var factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        HttpStatusCode status = await GetAsync(client, "/media/nope/nope/index.m3u8");

        Assert.Equal(HttpStatusCode.NotFound, status);
        CapturedLogEntry request = Assert.Single(RequestEvents(factory));
        Assert.Equal(LogLevel.Warning, request.Level);
        Assert.Equal(404, request.Properties["StatusCode"]);
    }

    [Theory]
    [InlineData("/signin-twitch")]
    [InlineData("/")]
    public async Task Query_strings_never_reach_the_request_event(string path)
    {
        using var factory = CreateFactory();
        using HttpClient client = factory.CreateClient(new() { AllowAutoRedirect = false });

        await GetAsync(client, $"{path}?code=SECRET-CODE&state=SECRET-STATE&returnUrl=/x");

        CapturedLogEntry request = Assert.Single(RequestEvents(factory));
        Assert.Equal(path, request.Properties["RequestPath"]);
        Assert.DoesNotContain("SECRET", EventText(request), StringComparison.Ordinal);
        Assert.DoesNotContain("returnUrl", EventText(request), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_exception_handled_by_the_error_page_logs_one_error_for_the_original_path()
    {
        using var factory = CreateFactory(services =>
        {
            services.RemoveAll<IProgressStore>();
            services.AddSingleton<IProgressStore, ThrowingProgressStore>();
        });
        using HttpClient client = factory.CreateClient();
        // The home page reads each recording's progress while it renders.
        factory.Services.GetRequiredService<RecordingLibrary>().Upsert(new RecordingSummary(
            "r1", "ann", "Title", "Game", StartedAt, StartedAt.AddHours(1), IsRecording: false,
            DurationSeconds: 3600, SizeBytes: 1, PartCount: 1, SegmentCount: 1,
            HasThumbnail: false, Chapters: []));

        using HttpResponseMessage response = await client.GetAsync("/");
        string body = await response.Content.ReadAsStringAsync();

        // The exception handler still renders the error page...
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("Something went wrong", body, StringComparison.Ordinal);
        // ...and request logging, outside it, records the original path's 500 once, at Error.
        CapturedLogEntry request = Assert.Single(RequestEvents(factory));
        Assert.Equal(LogLevel.Error, request.Level);
        Assert.Equal("/", request.Properties["RequestPath"]);
        Assert.Equal(500, request.Properties["StatusCode"]);
    }

    [Fact]
    public async Task An_aborted_live_index_request_logs_debug_not_error()
    {
        using var factory = CreateFactory(
            services => services.AddSingleton<IStartupFilter, CancelingMediaStartupFilter>(),
            new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Debug",
                ["LivePlaylist:RefreshSeconds"] = "3600",
            });
        using HttpClient client = factory.CreateClient();
        var live = new LiveStream("123456789", "Title", "Game", StartedAt, null);
        Session session = Session.For(live, "ann", factory.RecordingsRoot);
        Directory.CreateDirectory(session.Directory);
        await File.WriteAllTextAsync(
            Path.Combine(session.Directory, "p001.m3u8"),
            PlaylistTestData.LivePlaylist(
                6, "init_001.mp4", PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6)));
        await factory.Services.GetRequiredService<SessionStore>()
            .OpenAsync(session, live, CancellationToken.None);
        await factory.Services.GetRequiredService<LiveIndex>().RefreshOneAsync(
            factory.Services.GetRequiredService<RecordingLibrary>().Find(session.Id)!,
            CancellationToken.None);

        using HttpResponseMessage response = await client.GetAsync(
            $"/media/{session.Channel}/{session.Id}/index.m3u8");
        _ = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, (int)response.StatusCode);
        CapturedLogEntry request = Assert.Single(RequestEvents(factory));
        Assert.Equal(LogLevel.Debug, request.Level);
        Assert.Equal(StatusCodes.Status499ClientClosedRequest, request.Properties["StatusCode"]);
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Level == LogLevel.Error);
    }

    private static StreamKeeprAppFactory CreateFactory(
        Action<IServiceCollection>? configure = null,
        IReadOnlyDictionary<string, string?>? settings = null) =>
        new(settings, configureTestServices: services =>
        {
            services.AddSingleton<IStartupFilter, KestrelRawTargetFilter>();
            configure?.Invoke(services);
        });

    /// <summary>
    /// The request-completion events, picked out by their template. The Testing host forwards
    /// Serilog events to the factory's <see cref="ListLoggerProvider"/>.
    /// </summary>
    private static IReadOnlyList<CapturedLogEntry> RequestEvents(StreamKeeprAppFactory factory) =>
        factory.Logs.Entries
            .Where(entry => entry.Template == RequestLoggingPipeline.MessageTemplate)
            .ToArray();

    /// <summary>Everything an event would put in a log sink: its message and every property.</summary>
    private static string EventText(CapturedLogEntry entry) =>
        entry.Message + " " + string.Join(" ", entry.Properties.Select(pair =>
            $"{pair.Key}={Convert.ToString(pair.Value, CultureInfo.InvariantCulture)}"));

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, string uri)
    {
        using HttpResponseMessage response = await client.GetAsync(uri);
        _ = await response.Content.ReadAsByteArrayAsync();
        return response.StatusCode;
    }

    /// <summary>
    /// Marks a media request as client-aborted before the pipeline runs, so the endpoint sees a
    /// canceled <see cref="HttpContext.RequestAborted"/> and the request log sees a 499.
    /// </summary>
    private sealed class CancelingMediaStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Path.Value?.EndsWith(
                        "/index.m3u8", StringComparison.Ordinal) == true)
                {
                    context.RequestAborted = new CancellationToken(canceled: true);
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }

    /// <summary>
    /// Kestrel sets <see cref="IHttpRequestFeature.RawTarget"/> to the path and query as sent;
    /// the test server leaves it empty. Serilog logs <c>RawTarget</c> when the query is
    /// included, so without this an included query string would go unnoticed here.
    /// </summary>
    private sealed class KestrelRawTargetFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                IHttpRequestFeature request = context.Features.GetRequiredFeature<IHttpRequestFeature>();
                if (string.IsNullOrEmpty(request.RawTarget))
                    request.RawTarget = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    /// <summary>
    /// Fails the first read only: the error page re-renders the layout, which reads again.
    /// </summary>
    private sealed class ThrowingProgressStore : IProgressStore
    {
        private int _reads;

        public event Action? Changed { add { } remove { } }

        public double? Get(string recordingId) =>
            Interlocked.Increment(ref _reads) == 1
                ? throw new InvalidOperationException("progress store failure")
                : null;

        public Task SetAsync(string recordingId, double positionSec, CancellationToken ct) =>
            Task.CompletedTask;

        public Task ClearAsync(string recordingId, CancellationToken ct) => Task.CompletedTask;
    }
}
