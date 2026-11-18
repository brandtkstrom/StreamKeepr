using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Hosting;

/// <summary>
/// The request-completion event through the real pipeline. Each request body is read to the
/// end before asserting: the test server completes the body only after the pipeline returns,
/// so the completion event has already been written.
/// </summary>
public sealed class RequestLoggingHostTests
{
    private const string RequestLoggingCategory = "Serilog.AspNetCore.RequestLoggingMiddleware";
    private static readonly DateTimeOffset StartedAt = new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_page_request_logs_one_information_event_with_its_request_id()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = CreateFactory(capture);
        using HttpClient client = factory.CreateClient();

        await GetAsync(client, "/");

        RequestEvent request = Assert.Single(capture.Requests);
        Assert.Equal(LogLevel.Information, request.Level);
        Assert.Equal("GET", request.Property("RequestMethod"));
        Assert.Equal("/", request.Property("RequestPath"));
        Assert.Equal("200", request.Property("StatusCode"));
        Assert.NotNull(request.Property("Elapsed"));
        Assert.False(string.IsNullOrWhiteSpace(request.Property("RequestId")));
        Assert.Contains(request.Property("RequestId")!, request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_successful_health_probe_logs_no_request_event()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = CreateFactory(capture);
        using HttpClient client = factory.CreateClient();

        HttpStatusCode health = await GetAsync(client, "/health/live");
        await GetAsync(client, "/");

        Assert.Equal(HttpStatusCode.OK, health);
        // The page request proves request logging is live; the probe left nothing behind.
        Assert.Equal(["/"], capture.Requests.Select(request => request.Property("RequestPath")));
    }

    [Fact]
    public async Task A_missing_media_file_logs_a_warning()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = CreateFactory(capture);
        using HttpClient client = factory.CreateClient();

        HttpStatusCode status = await GetAsync(client, "/media/nope/nope/index.m3u8");

        Assert.Equal(HttpStatusCode.NotFound, status);
        RequestEvent request = Assert.Single(capture.Requests);
        Assert.Equal(LogLevel.Warning, request.Level);
        Assert.Equal("404", request.Property("StatusCode"));
    }

    [Theory]
    [InlineData("/signin-twitch")]
    [InlineData("/")]
    public async Task Query_strings_never_reach_the_request_event(string path)
    {
        var capture = new CapturingLoggerProvider();
        using var factory = CreateFactory(capture);
        using HttpClient client = factory.CreateClient(new() { AllowAutoRedirect = false });

        await GetAsync(client, $"{path}?code=SECRET-CODE&state=SECRET-STATE&returnUrl=/x");

        RequestEvent request = Assert.Single(capture.Requests);
        Assert.Equal(path, request.Property("RequestPath"));
        Assert.DoesNotContain("SECRET", request.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("returnUrl", request.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_exception_handled_by_the_error_page_logs_one_error_for_the_original_path()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = CreateFactory(capture, services =>
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
        RequestEvent request = Assert.Single(capture.Requests);
        Assert.Equal(LogLevel.Error, request.Level);
        Assert.Equal("/", request.Property("RequestPath"));
        Assert.Equal("500", request.Property("StatusCode"));
    }

    private static StreamKeeprAppFactory CreateFactory(
        CapturingLoggerProvider capture, Action<IServiceCollection>? configure = null) =>
        new(configureTestServices: services =>
        {
            // The Testing host forwards Serilog events to every registered provider.
            services.AddSingleton<ILoggerProvider>(capture);
            services.AddSingleton<IStartupFilter, KestrelRawTargetFilter>();
            configure?.Invoke(services);
        });

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, string uri)
    {
        using HttpResponseMessage response = await client.GetAsync(uri);
        _ = await response.Content.ReadAsByteArrayAsync();
        return response.StatusCode;
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

    private sealed record RequestEvent(
        LogLevel Level, string Message, IReadOnlyDictionary<string, string?> Properties)
    {
        public string? Property(string name) => Properties.GetValueOrDefault(name);

        public string Text =>
            Message + " " + string.Join(" ", Properties.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    /// <summary>
    /// Keeps level and structured properties for request events only. The shared
    /// <see cref="ListLoggerProvider"/> records rendered text, which cannot show a level.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<RequestEvent> _requests = new();

        public IReadOnlyList<RequestEvent> Requests => _requests.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (category != RequestLoggingCategory)
                    return;

                var properties = (state as IEnumerable<KeyValuePair<string, object?>> ?? [])
                    .ToDictionary(pair => pair.Key, pair => Render(pair.Value), StringComparer.Ordinal);
                owner._requests.Enqueue(new RequestEvent(logLevel, formatter(state, exception), properties));
            }

            private static string? Render(object? value) => value switch
            {
                null => null,
                ScalarValue scalar => Convert.ToString(scalar.Value, System.Globalization.CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            };
        }
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
