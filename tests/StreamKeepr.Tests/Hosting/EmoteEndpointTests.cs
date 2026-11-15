using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Emotes;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Emotes;

namespace StreamKeepr.Tests.Hosting;

public sealed class EmoteEndpointTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private static readonly RecordingSummary Summary = new(
        "20261115T090000Z_1",
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

    private const string GlobalJson = """
        [
          {"id":"54fa8f1401e468494b85b537","code":"KEKW","animated":false,"modifier":false}
        ]
        """;

    [Fact]
    public async Task Enabled_endpoint_returns_the_global_catalog_envelope()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        using StreamKeeprAppFactory factory = CreateFactory(handler, enabled: true);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = json.RootElement;
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.EndsWith("Z", root.GetProperty("fetchedAt").GetString(), StringComparison.Ordinal);

        JsonElement entry = Assert.Single(root.GetProperty("entries").EnumerateArray());
        Assert.Equal("bttv", entry.GetProperty("provider").GetString());
        Assert.Equal("54fa8f1401e468494b85b537", entry.GetProperty("id").GetString());
        Assert.Equal("KEKW", entry.GetProperty("code").GetString());
        Assert.False(entry.GetProperty("animated").GetBoolean());
        Assert.Equal(
            "https://cdn.betterttv.net/emote/54fa8f1401e468494b85b537/1x.webp",
            entry.GetProperty("src").GetString());
        Assert.Equal(
            "https://cdn.betterttv.net/emote/54fa8f1401e468494b85b537/1x.webp 1x, " +
            "https://cdn.betterttv.net/emote/54fa8f1401e468494b85b537/2x.webp 2x, " +
            "https://cdn.betterttv.net/emote/54fa8f1401e468494b85b537/3x.webp 3x",
            entry.GetProperty("srcset").GetString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Unknown_recordings_return_404_without_calling_a_provider()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        using StreamKeeprAppFactory factory = CreateFactory(handler, enabled: true);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/recordings/missing/emotes");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_disabled_feature_returns_an_empty_catalog_without_a_request()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        using StreamKeeprAppFactory factory = CreateFactory(handler, enabled: false);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = json.RootElement;
        Assert.False(root.GetProperty("available").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("fetchedAt").ValueKind);
        Assert.Empty(root.GetProperty("entries").EnumerateArray());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_provider_failure_returns_an_empty_catalog_and_never_an_error()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using StreamKeeprAppFactory factory = CreateFactory(handler, enabled: true);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = json.RootElement;
        Assert.False(root.GetProperty("available").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("fetchedAt").ValueKind);
        Assert.Empty(root.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task A_failed_refresh_serves_the_stale_catalog()
    {
        var fail = false;
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            fail
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : EmoteTestData.Json(GlobalJson)));
        var clock = new FakeTimeProvider(Now);
        using StreamKeeprAppFactory factory = CreateFactory(handler, enabled: true, clock);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage first =
            await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using JsonDocument firstJson =
            JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        string fetchedAt = firstJson.RootElement.GetProperty("fetchedAt").GetString()!;

        clock.Advance(TimeSpan.FromMinutes(5));
        fail = true;
        using HttpResponseMessage second =
            await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using JsonDocument secondJson =
            JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        JsonElement root = secondJson.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.True(root.GetProperty("stale").GetBoolean());
        Assert.Equal(fetchedAt, root.GetProperty("fetchedAt").GetString());
        Assert.Single(root.GetProperty("entries").EnumerateArray());
        Assert.Equal(2, handler.Requests.Count);
    }

    private static StreamKeeprAppFactory CreateFactory(
        HttpMessageHandler handler, bool enabled, TimeProvider? clock = null)
    {
        var overrides = new Dictionary<string, string?>
        {
            [$"{ChatOptions.SectionName}:Emotes:Bttv:Enabled"] = enabled ? "true" : "false",
        };

        var factory = new StreamKeeprAppFactory(
            overrides,
            configureTestServices: services =>
            {
                services.AddHttpClient(EmoteHttpClients.Bttv)
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
                if (clock is not null)
                    services.AddSingleton(clock);
            });

        factory.Services.GetRequiredService<RecordingLibrary>().Upsert(Summary);
        return factory;
    }
}
