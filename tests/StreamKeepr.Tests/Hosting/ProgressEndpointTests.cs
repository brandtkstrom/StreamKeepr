using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Hosting;

public sealed class ProgressEndpointTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_valid_position_is_stored_and_returns_no_content()
    {
        using var factory = new StreamKeeprAppFactory();
        using HttpClient client = factory.CreateClient();
        Seed(factory, "r1");

        using HttpResponseMessage response = await PutAsync(client, "r1", """{"positionSec":125.5}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(125.5, factory.Services.GetRequiredService<IProgressStore>().Get("r1"));
    }

    [Theory]
    [InlineData("""{"positionSec":-1}""")]
    [InlineData("""{"positionSec":"NaN"}""")]
    [InlineData("""{"positionSec":1e400}""")]
    [InlineData("""{}""")]
    [InlineData("""not json""")]
    public async Task Invalid_positions_are_rejected(string body)
    {
        using var factory = new StreamKeeprAppFactory();
        using HttpClient client = factory.CreateClient();
        Seed(factory, "r1");

        using HttpResponseMessage response = await PutAsync(client, "r1", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Null(factory.Services.GetRequiredService<IProgressStore>().Get("r1"));
    }

    [Fact]
    public async Task An_unknown_recording_is_not_found()
    {
        using var factory = new StreamKeeprAppFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PutAsync(client, "nope", """{"positionSec":5}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    private static void Seed(StreamKeeprAppFactory factory, string id) =>
        factory.Services.GetRequiredService<RecordingLibrary>().Upsert(new RecordingSummary(
            id, "ann", "Title", "Game", StartedAt, StartedAt.AddHours(1), IsRecording: false,
            DurationSeconds: 3600, SizeBytes: 1, PartCount: 1, SegmentCount: 1,
            HasThumbnail: false, Chapters: []));

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string id, string json) =>
        client.PutAsync(
            $"/api/recordings/{id}/progress",
            new StringContent(json, Encoding.UTF8, "application/json"));
}
