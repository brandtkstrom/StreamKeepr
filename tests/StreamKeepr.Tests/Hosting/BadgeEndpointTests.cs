using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Badges;
using StreamKeepr.Tests.Emotes;

namespace StreamKeepr.Tests.Hosting;

public sealed class BadgeEndpointTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private static readonly RecordingSummary Summary = new(
        "20261125T090000Z_1",
        "chan",
        "Title",
        "Game",
        Now,
        Now.AddMinutes(1),
        IsRecording: false,
        DurationSeconds: 60,
        SizeBytes: 1,
        PartCount: 1,
        SegmentCount: 2,
        HasThumbnail: false,
        Chapters: [],
        HasChat: false);

    private const string CatalogJson = """
        {
          "data": [
            {
              "set_id": "moderator",
              "versions": [
                {
                  "id": "1",
                  "image_url_1x": "https://static-cdn.jtvnw.net/badges/v1/moderator/1",
                  "image_url_2x": "https://static-cdn.jtvnw.net/badges/v1/moderator/2",
                  "image_url_4x": "https://static-cdn.jtvnw.net/badges/v1/moderator/3",
                  "title": "Moderator",
                  "description": "A moderator"
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task Returns_the_global_catalog_envelope_without_a_credential()
    {
        var handler = Handler(() => EmoteTestData.Json(CatalogJson));
        using StreamKeeprAppFactory factory = CreateFactory(handler);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/badges");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.EndsWith("Z", root.GetProperty("fetchedAt").GetString(), StringComparison.Ordinal);
        Assert.Empty(root.GetProperty("channel").EnumerateArray());

        JsonElement entry = Assert.Single(root.GetProperty("global").EnumerateArray());
        Assert.Equal("moderator", entry.GetProperty("set").GetString());
        Assert.Equal("1", entry.GetProperty("version").GetString());
        Assert.Equal("Moderator", entry.GetProperty("title").GetString());
        Assert.Equal(
            "https://static-cdn.jtvnw.net/badges/v1/moderator/1",
            entry.GetProperty("src").GetString());
        Assert.Equal(
            "https://static-cdn.jtvnw.net/badges/v1/moderator/1 1x, " +
            "https://static-cdn.jtvnw.net/badges/v1/moderator/2 2x, " +
            "https://static-cdn.jtvnw.net/badges/v1/moderator/3 4x",
            entry.GetProperty("srcset").GetString());

        // The browser response never carries the token, the secret, or the raw envelope.
        Assert.DoesNotContain("test-client-secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", body, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", body, StringComparison.Ordinal);

        CapturedRequest badge = Assert.Single(
            handler.Requests,
            request => request.RequestUri?.Host == "api.twitch.tv");
        Assert.Contains(
            badge.Headers,
            header => header.Key == "Client-Id" && header.Value == "test-client-id");
        Assert.Contains(
            badge.Headers,
            header => header.Key == "Authorization" && header.Value == "Bearer one");
    }

    [Fact]
    public async Task Unknown_recordings_return_404_without_calling_twitch()
    {
        var handler = Handler(() => EmoteTestData.Json(CatalogJson));
        using StreamKeeprAppFactory factory = CreateFactory(handler);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync("/api/recordings/missing/badges");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_source_failure_returns_an_unavailable_catalog_and_never_an_error()
    {
        var handler = Handler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using StreamKeeprAppFactory factory = CreateFactory(handler);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/badges");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = json.RootElement;
        Assert.False(root.GetProperty("available").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("fetchedAt").ValueKind);
        Assert.Empty(root.GetProperty("global").EnumerateArray());
        Assert.Empty(root.GetProperty("channel").EnumerateArray());

        using HttpResponseMessage health = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task An_invalid_payload_returns_an_unavailable_catalog()
    {
        var handler = Handler(() => EmoteTestData.Json("not json"));
        using StreamKeeprAppFactory factory = CreateFactory(handler);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/badges");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = json.RootElement;
        Assert.False(root.GetProperty("available").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.Empty(root.GetProperty("global").EnumerateArray());
        Assert.Empty(root.GetProperty("channel").EnumerateArray());
    }

    [Fact]
    public async Task A_failed_refresh_serves_the_stale_catalog()
    {
        var fail = false;
        var handler = Handler(() => fail
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : EmoteTestData.Json(CatalogJson));
        var clock = new FakeTimeProvider(Now);
        using StreamKeeprAppFactory factory = CreateFactory(handler, clock);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage first =
            await client.GetAsync($"/api/recordings/{Summary.Id}/badges");
        using JsonDocument firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        string fetchedAt = firstJson.RootElement.GetProperty("fetchedAt").GetString()!;

        clock.Advance(TimeSpan.FromMinutes(60));
        fail = true;
        using HttpResponseMessage second =
            await client.GetAsync($"/api/recordings/{Summary.Id}/badges");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using JsonDocument secondJson =
            JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        JsonElement root = secondJson.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.True(root.GetProperty("stale").GetBoolean());
        Assert.Equal(fetchedAt, root.GetProperty("fetchedAt").GetString());
        Assert.Single(root.GetProperty("global").EnumerateArray());
    }

    [Fact]
    public async Task The_catalog_is_fetched_once_across_requests()
    {
        var handler = Handler(() => EmoteTestData.Json(CatalogJson));
        using StreamKeeprAppFactory factory = CreateFactory(handler);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage first =
            await client.GetAsync($"/api/recordings/{Summary.Id}/badges");
        using HttpResponseMessage second =
            await client.GetAsync($"/api/recordings/{Summary.Id}/badges");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Single(
            handler.Requests,
            request => request.RequestUri?.Host == "api.twitch.tv");
    }

    private static StreamKeeprAppFactory CreateFactory(
        HttpMessageHandler handler, TimeProvider? clock = null)
    {
        var factory = new StreamKeeprAppFactory(
            configureTestServices: services =>
            {
                services.AddHttpClient(TwitchHttpClients.Helix)
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
                services.AddHttpClient(TwitchHttpClients.Auth)
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
                if (clock is not null)
                    services.AddSingleton(clock);
            });

        factory.Services.GetRequiredService<RecordingLibrary>().Upsert(Summary);
        return factory;
    }

    /// <summary>Answers token acquisition, then delegates the badge request to the test.</summary>
    private static StubHttpMessageHandler Handler(Func<HttpResponseMessage> badge) =>
        new((request, _) => Task.FromResult(
            request.RequestUri!.Host == "id.twitch.tv"
                ? BadgeTestData.Token()
                : badge()));

}
