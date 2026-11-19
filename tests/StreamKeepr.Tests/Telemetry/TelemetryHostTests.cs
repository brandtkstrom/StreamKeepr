using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using StreamKeepr.Infrastructure.Telemetry;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Telemetry;

[Collection("Telemetry")]
public sealed class TelemetryHostTests
{
    [Fact]
    public async Task Telemetry_is_off_by_default()
    {
        var custom = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StreamKeeprTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (custom)
                    custom.Add(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var factory = new StreamKeeprAppFactory();

        Assert.Null(factory.Services.GetService<TracerProvider>());
        Assert.Null(factory.Services.GetService<MeterProvider>());
        Assert.DoesNotContain(
            factory.Services.GetServices<ILoggerProvider>(),
            provider => provider.GetType().Namespace?.Contains("OpenTelemetry", StringComparison.Ordinal) == true);

        using var client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(custom);
    }

    [Theory]
    [InlineData("OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION")]
    [InlineData("OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION")]
    public void Enabled_telemetry_refuses_disabled_query_redaction(string variable)
    {
        using var redaction = SetVariable(variable, "true");
        using var factory = new StreamKeeprAppFactory(telemetry: true);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => factory.CreateClient());

        Assert.Contains("redaction", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enabled_host_emits_a_server_span_and_filters_sensitive_paths()
    {
        using var batchDelay = SetVariable("OTEL_BSP_SCHEDULE_DELAY", "50");
        var exported = new List<Activity>();
        using var factory = new StreamKeeprAppFactory(
            configureTestServices: services => services
                .ConfigureOpenTelemetryTracerProvider(
                    tracing => tracing.AddInMemoryExporter(exported)),
            telemetry: true);

        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using (HttpResponseMessage home = await client.GetAsync("/"))
            Assert.Equal(HttpStatusCode.OK, home.StatusCode);

        using (HttpResponseMessage health = await client.GetAsync("/health/live"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using (HttpResponseMessage signIn = await client.GetAsync("/signin-twitch?code=secret&state=secret"))
            _ = signIn.StatusCode;

        using (HttpResponseMessage media = await client.GetAsync("/media/streamer/id/index.m3u8"))
            _ = media.StatusCode;

        using (HttpResponseMessage blazor = await client.GetAsync("/_blazor"))
            _ = blazor.StatusCode;

        Activity[] spans = [.. exported];

        Assert.True(
            spans.Any(
                span => span.Kind == ActivityKind.Server
                    && span.DisplayName.Contains("GET", StringComparison.Ordinal)
                    && Equals(span.GetTagItem("url.path"), "/")),
            string.Join(
                " | ",
                spans.Select(
                    span => $"{span.Kind} {span.OperationName} route={span.GetTagItem("http.route")} path={span.GetTagItem("url.path")}")));

        Assert.DoesNotContain(
            spans,
            span => span.GetTagItem("url.path")?.ToString()?.Contains("/health", StringComparison.Ordinal) == true
                || span.GetTagItem("url.path")?.ToString()?.Contains("/media", StringComparison.Ordinal) == true
                || span.GetTagItem("url.path")?.ToString()?.Contains("/_blazor", StringComparison.Ordinal) == true
                || span.GetTagItem("url.path")?.ToString()?.Contains("/signin-twitch", StringComparison.Ordinal) == true);

        Assert.DoesNotContain(
            spans,
            span => span.GetTagItem("url.query")?.ToString()?.Contains("secret", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Enabled_host_shuts_down_within_the_budget()
    {
        var factory = new StreamKeeprAppFactory(telemetry: true);
        using (HttpClient client = factory.CreateClient())
        {
        }

        var elapsed = Stopwatch.StartNew();
        factory.Dispose();

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(50),
            $"telemetry shutdown took {elapsed.Elapsed}");
    }

    [Fact]
    public async Task Enabled_host_records_http_server_duration_but_not_for_health()
    {
        using var factory = new StreamKeeprAppFactory(telemetry: true);
        using var client = factory.CreateClient();

        var factoryMeter = factory.Services.GetRequiredService<IMeterFactory>();
        using var duration = new MetricCollector<double>(
            factoryMeter, "Microsoft.AspNetCore.Hosting", "http.server.request.duration");

        using (HttpResponseMessage home = await client.GetAsync("/"))
            Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        await duration.WaitForMeasurementsAsync(1, TimeSpan.FromSeconds(5));
        duration.Clear();

        using (HttpResponseMessage health = await client.GetAsync("/health/live"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using (HttpResponseMessage ready = await client.GetAsync("/health/ready"))
            _ = ready.StatusCode;
        using (HttpResponseMessage media = await client.GetAsync("/media/streamer/id/index.m3u8"))
            _ = media.StatusCode;
        using (HttpResponseMessage blazor = await client.GetAsync("/_blazor"))
            _ = blazor.StatusCode;

        Assert.Empty(duration.GetMeasurementSnapshot());
    }

    private static IDisposable SetVariable(string name, string? value)
    {
        string? previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        return new EnvironmentScope(name, previous);
    }

    private sealed class EnvironmentScope(string name, string? previous) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable(name, previous);
    }

}
