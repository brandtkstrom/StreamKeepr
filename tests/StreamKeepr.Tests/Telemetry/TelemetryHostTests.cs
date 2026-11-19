using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Telemetry;

[Collection("Telemetry")]
public sealed class TelemetryHostTests
{
    [Fact]
    public void Telemetry_is_off_by_default()
    {
        using var telemetry = SetTelemetry(null);
        using var factory = new StreamKeeprAppFactory();

        Assert.Null(factory.Services.GetService<TracerProvider>());
        Assert.Null(factory.Services.GetService<MeterProvider>());
        Assert.DoesNotContain(
            factory.Services.GetServices<ILoggerProvider>(),
            provider => provider.GetType().Namespace?.Contains("OpenTelemetry", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Enabled_telemetry_refuses_disabled_query_redaction()
    {
        using var telemetry = SetTelemetry("true");
        using var redaction = SetVariable(
            "OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION", "true");
        using var factory = new StreamKeeprAppFactory();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => factory.CreateClient());

        Assert.Contains("redaction", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enabled_host_emits_a_server_span_and_filters_sensitive_paths()
    {
        using var telemetry = SetTelemetry("true");
        using var batchDelay = SetVariable("OTEL_BSP_SCHEDULE_DELAY", "50");
        var exported = new List<Activity>();
        using var factory = new StreamKeeprAppFactory(
            new Dictionary<string, string?>
            {
                ["Telemetry:Enabled"] = "true",
            },
            configureTestServices: services => services
                .ConfigureOpenTelemetryTracerProvider(
                    tracing => tracing.AddInMemoryExporter(exported)));

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

        Activity[] spans = await WaitForSpansAsync(exported, atLeast: 1);

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
    public async Task Enabled_host_records_http_server_duration_but_not_for_health()
    {
        using var telemetry = SetTelemetry("true");
        using var factory = new StreamKeeprAppFactory(
            new Dictionary<string, string?>
            {
                ["Telemetry:Enabled"] = "true",
            });
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

        await Task.Delay(100);
        Assert.Empty(duration.GetMeasurementSnapshot());
    }

    private static IDisposable SetTelemetry(string? value) =>
        SetVariable("Telemetry__Enabled", value);

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

    private static async Task<Activity[]> WaitForSpansAsync(
        List<Activity> exported, int atLeast)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (exported.Count < atLeast && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        return [.. exported];
    }
}
