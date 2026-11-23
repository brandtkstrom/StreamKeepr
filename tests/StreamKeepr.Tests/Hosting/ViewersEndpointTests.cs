using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Hosting;

public sealed class ViewersEndpointTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_finished_recording_returns_its_samples_and_whole_second_final_duration_without_caching()
    {
        using StreamKeeprAppFactory factory = CreateFactory();
        RecordingSummary summary = Upsert(factory, Finished(durationSeconds: 8047.4));
        WriteViewers(factory, summary,
            """{"v":1,"at":0,"count":1658}""",
            """{"v":1,"at":60.5,"count":1700}""");
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync($"/api/recordings/{summary.Id}/viewers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            """{"v":1,"recordingId":"20261115T090000Z_1","durationSec":8047,"samples":[{"at":0,"count":1658},{"at":60.5,"count":1700}]}""",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_live_recording_reports_its_current_duration_in_whole_seconds()
    {
        var clock = new FakeTimeProvider(StartedAt.AddSeconds(90.6));
        using StreamKeeprAppFactory factory = CreateFactory(clock);
        RecordingSummary summary = Upsert(factory, Finished(durationSeconds: 0) with
        {
            EndedAt = null,
            IsRecording = true,
            DurationSeconds = null,
        });
        WriteViewers(factory, summary, """{"v":1,"at":0,"count":12}""");
        using HttpClient client = factory.CreateClient();

        using JsonDocument json = await GetJsonAsync(client, summary.Id);

        Assert.Equal("91", json.RootElement.GetProperty("durationSec").GetRawText());
        Assert.Equal(1, json.RootElement.GetProperty("samples").GetArrayLength());
    }

    [Fact]
    public async Task A_recording_without_a_viewer_log_returns_no_samples()
    {
        using StreamKeeprAppFactory factory = CreateFactory();
        RecordingSummary summary = Upsert(factory, Finished(durationSeconds: 60));
        using HttpClient client = factory.CreateClient();

        using JsonDocument json = await GetJsonAsync(client, summary.Id);

        Assert.Equal(1, json.RootElement.GetProperty("v").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("samples").GetArrayLength());
    }

    [Fact]
    public async Task Malformed_lines_are_skipped_with_a_warning()
    {
        using StreamKeeprAppFactory factory = CreateFactory();
        RecordingSummary summary = Upsert(factory, Finished(durationSeconds: 120));
        WriteViewers(factory, summary,
            """{"v":1,"at":0,"count":5}""",
            "{\"v\":1,\"at\":",
            """{"v":1,"at":60,"count":6}""");
        using HttpClient client = factory.CreateClient();

        using JsonDocument json = await GetJsonAsync(client, summary.Id);

        int[] counts = json.RootElement.GetProperty("samples").EnumerateArray()
            .Select(sample => sample.GetProperty("count").GetInt32())
            .ToArray();
        Assert.Equal([5, 6], counts);
        Assert.Contains(
            factory.Logs.Messages,
            message => message.Contains("Skipped 1 malformed viewer sample", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("20261115T090000Z_404")]
    [InlineData("missing")]
    public async Task Unknown_recordings_return_404_without_caching(string id)
    {
        using StreamKeeprAppFactory factory = CreateFactory();
        Upsert(factory, Finished(durationSeconds: 60));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync($"/api/recordings/{id}/viewers");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    private static StreamKeeprAppFactory CreateFactory(TimeProvider? clock = null) =>
        new(
            // No allowlist: viewing is open (ADR-0013), and admin routes such as export download
            // still need a sign-in, so a 200 here proves the route is public while open.
            overrides: null,
            configureTestServices: services =>
            {
                if (clock is not null)
                    services.AddSingleton(clock);
            });

    private static RecordingSummary Finished(double durationSeconds) => new(
        "20261115T090000Z_1",
        "chan",
        "Title",
        "Game",
        StartedAt,
        StartedAt.AddSeconds(durationSeconds),
        IsRecording: false,
        DurationSeconds: durationSeconds,
        SizeBytes: 1,
        PartCount: 1,
        SegmentCount: 2,
        HasThumbnail: false,
        Chapters: []);

    private static RecordingSummary Upsert(StreamKeeprAppFactory factory, RecordingSummary summary)
    {
        factory.Services.GetRequiredService<RecordingLibrary>().Upsert(summary);
        return summary;
    }

    private static void WriteViewers(
        StreamKeeprAppFactory factory, RecordingSummary summary, params string[] lines)
    {
        string directory = Path.Combine(factory.RecordingsRoot, summary.Channel, summary.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, ViewerSamples.FileName), lines);
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string id)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/recordings/{id}/viewers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
