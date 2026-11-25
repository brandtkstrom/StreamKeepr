using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Emotes;
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

    private const string GlobalWithSubscriberJson = """
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
                  "title": "Moderator"
                }
              ]
            },
            {
              "set_id": "subscriber",
              "versions": [
                {
                  "id": "0",
                  "image_url_1x": "https://static-cdn.jtvnw.net/badges/v1/subscriber/1",
                  "image_url_2x": "https://static-cdn.jtvnw.net/badges/v1/subscriber/2",
                  "image_url_4x": "https://static-cdn.jtvnw.net/badges/v1/subscriber/3",
                  "title": "Subscriber"
                }
              ]
            }
          ]
        }
        """;

    private const string ChannelCatalogJson = """
        {
          "data": [
            {
              "set_id": "subscriber",
              "versions": [
                {
                  "id": "0",
                  "image_url_1x": "https://static-cdn.jtvnw.net/badges/v1/channel/sub0/1",
                  "image_url_2x": "https://static-cdn.jtvnw.net/badges/v1/channel/sub0/2",
                  "image_url_4x": "https://static-cdn.jtvnw.net/badges/v1/channel/sub0/3",
                  "title": "Nova Subscriber"
                },
                {
                  "id": "3",
                  "image_url_1x": "https://static-cdn.jtvnw.net/badges/v1/channel/sub3/1",
                  "image_url_2x": "https://static-cdn.jtvnw.net/badges/v1/channel/sub3/2",
                  "image_url_4x": "https://static-cdn.jtvnw.net/badges/v1/channel/sub3/3",
                  "title": "Nova Subscriber 3 months"
                }
              ]
            },
            {
              "set_id": "bits",
              "versions": [
                {
                  "id": "100",
                  "image_url_1x": "https://static-cdn.jtvnw.net/badges/v1/channel/bits100/1",
                  "image_url_2x": "https://static-cdn.jtvnw.net/badges/v1/channel/bits100/2",
                  "image_url_4x": "https://static-cdn.jtvnw.net/badges/v1/channel/bits100/3",
                  "title": "100 Bits"
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
    public async Task Returns_channel_badges_beside_global_badges_for_the_resolved_broadcaster()
    {
        var resolver = new FakeEmoteChannelResolver("9100001");
        var handler = Handler(request => IsChannelRequest(request)
            ? EmoteTestData.Json(ChannelCatalogJson)
            : EmoteTestData.Json(GlobalWithSubscriberJson));
        using StreamKeeprAppFactory factory = CreateFactory(handler, resolver: resolver);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/badges");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = json.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());

        JsonElement[] global = root.GetProperty("global").EnumerateArray().ToArray();
        Assert.Equal(
            ["moderator/1", "subscriber/0"],
            global.Select(entry => $"{entry.GetProperty("set").GetString()}/{entry.GetProperty("version").GetString()}"));

        // The channel list stays separate and keeps the channel's own version of a shared set.
        JsonElement[] channel = root.GetProperty("channel").EnumerateArray().ToArray();
        Assert.Equal(
            ["subscriber/0", "subscriber/3", "bits/100"],
            channel.Select(entry => $"{entry.GetProperty("set").GetString()}/{entry.GetProperty("version").GetString()}"));
        Assert.Equal("Nova Subscriber", channel[0].GetProperty("title").GetString());
        Assert.Equal(
            "https://static-cdn.jtvnw.net/badges/v1/channel/sub0/1",
            channel[0].GetProperty("src").GetString());
        Assert.Equal("100 Bits", channel[2].GetProperty("title").GetString());

        // The catalog has no subscriber/12 in either scope; the pane must use the text chip.
        Assert.DoesNotContain(
            global.Concat(channel),
            entry => entry.GetProperty("set").GetString() == "subscriber" &&
                entry.GetProperty("version").GetString() == "12");

        CapturedRequest channelRequest = Assert.Single(
            handler.Requests,
            request => request.RequestUri?.Host == "api.twitch.tv" &&
                request.RequestUri.AbsolutePath == "/helix/chat/badges");
        Assert.Equal(
            "https://api.twitch.tv/helix/chat/badges?broadcaster_id=9100001",
            channelRequest.RequestUri!.AbsoluteUri);
        Assert.Equal(1, resolver.Lookups);
    }

    [Fact]
    public async Task An_unresolved_channel_returns_global_badges_only()
    {
        var resolver = new FakeEmoteChannelResolver();
        var handler = Handler(() => EmoteTestData.Json(CatalogJson));
        using StreamKeeprAppFactory factory = CreateFactory(handler, resolver: resolver);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/badges");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = json.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.Single(root.GetProperty("global").EnumerateArray());
        Assert.Empty(root.GetProperty("channel").EnumerateArray());
        Assert.Single(
            handler.Requests,
            request => request.RequestUri?.Host == "api.twitch.tv");
        Assert.Equal(1, resolver.Lookups);
    }

    [Fact]
    public async Task A_channel_failure_keeps_the_global_badges()
    {
        var handler = Handler(request => IsChannelRequest(request)
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : EmoteTestData.Json(GlobalWithSubscriberJson));
        using StreamKeeprAppFactory factory = CreateFactory(
            handler, resolver: new FakeEmoteChannelResolver("9100001"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/badges");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = json.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.Equal(2, root.GetProperty("global").GetArrayLength());
        Assert.Empty(root.GetProperty("channel").EnumerateArray());
    }

    [Fact]
    public async Task A_global_failure_keeps_the_cached_channel_catalog()
    {
        var failGlobal = false;
        var handler = Handler(request => IsChannelRequest(request)
            ? EmoteTestData.Json(ChannelCatalogJson)
            : failGlobal
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : EmoteTestData.Json(GlobalWithSubscriberJson));
        var clock = new FakeTimeProvider(Now);
        using StreamKeeprAppFactory factory = CreateFactory(
            handler, clock, new FakeEmoteChannelResolver("9100001"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage first =
            await client.GetAsync($"/api/recordings/{Summary.Id}/badges");
        using JsonDocument firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        string fetchedAt = firstJson.RootElement.GetProperty("fetchedAt").GetString()!;

        clock.Advance(TimeSpan.FromMinutes(60));
        failGlobal = true;
        using HttpResponseMessage second =
            await client.GetAsync($"/api/recordings/{Summary.Id}/badges");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using JsonDocument secondJson =
            JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        JsonElement root = secondJson.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.True(root.GetProperty("stale").GetBoolean());
        Assert.Equal(fetchedAt, root.GetProperty("fetchedAt").GetString());
        Assert.Equal(2, root.GetProperty("global").GetArrayLength());
        Assert.Equal(3, root.GetProperty("channel").GetArrayLength());
    }

    [Fact]
    public async Task An_invalid_channel_payload_keeps_the_global_badges()
    {
        var handler = Handler(request => IsChannelRequest(request)
            ? EmoteTestData.Json("not json")
            : EmoteTestData.Json(GlobalWithSubscriberJson));
        using StreamKeeprAppFactory factory = CreateFactory(
            handler, resolver: new FakeEmoteChannelResolver("9100001"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/badges");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = json.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.Equal(2, root.GetProperty("global").GetArrayLength());
        Assert.Empty(root.GetProperty("channel").EnumerateArray());
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
        HttpMessageHandler handler,
        TimeProvider? clock = null,
        FakeEmoteChannelResolver? resolver = null)
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
                services.AddSingleton<IEmoteChannelResolver>(
                    resolver ?? new FakeEmoteChannelResolver());
            });

        factory.Services.GetRequiredService<RecordingLibrary>().Upsert(Summary);
        return factory;
    }

    /// <summary>Answers token acquisition, then delegates the badge request to the test.</summary>
    private static StubHttpMessageHandler Handler(Func<HttpResponseMessage> badge) =>
        Handler(_ => badge());

    /// <summary>Answers token acquisition, then routes each badge request by path.</summary>
    private static StubHttpMessageHandler Handler(
        Func<HttpRequestMessage, HttpResponseMessage> badge) =>
        new((request, _) => Task.FromResult(
            request.RequestUri!.Host == "id.twitch.tv"
                ? BadgeTestData.Token()
                : badge(request)));

    private static bool IsChannelRequest(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath == "/helix/chat/badges";

}
