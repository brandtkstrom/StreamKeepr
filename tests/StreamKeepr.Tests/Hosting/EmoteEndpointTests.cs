using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Emotes;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Emotes;
using StreamKeepr.Tests.Runtime;

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

    private const string ChannelJson = """
        {
          "id": "5d1b7c0e95e8e11f4f6f0a1c",
          "channelEmotes": [
            {"id":"aaaaaaaaaaaaaaaaaaaaaaaa","code":"CHANNEL","animated":false,"modifier":false}
          ],
          "sharedEmotes": [
            {"id":"bbbbbbbbbbbbbbbbbbbbbbbb","code":"SHARED","animated":false,"modifier":false}
          ]
        }
        """;

    private const string SevenTvGlobalJson = """
        {
          "id": "01HKQT8EWR000ESSWF3625XCS4",
          "name": "Global Emotes",
          "emotes": [
            { "id": "01GGD5PJA8000FH13S498E9D8X", "name": "ppL", "flags": 0, "data": { "name": "ppL", "animated": false } }
          ]
        }
        """;

    private const string SevenTvChannelJson = """
        {
          "id": "71092938",
          "emote_set_id": "01FE9DRF000009TR6M9N941CYW",
          "emote_set": {
            "id": "01FE9DRF000009TR6M9N941CYW",
            "emotes": [
              { "id": "01G3WEGZN0000ET2J0MQP5YJ0G", "name": "CHANNELTV", "flags": 0, "data": { "name": "CHANNELTV", "animated": false } }
            ]
          }
        }
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

    [Fact]
    public async Task Enabled_endpoint_returns_channel_entries_before_global_entries()
    {
        var resolver = new FakeEmoteChannelResolver("12345");
        var handler = new StubHttpMessageHandler((request, _) =>
            Task.FromResult(EmoteTestData.Json(
                request.RequestUri!.AbsolutePath.Contains("/users/twitch/", StringComparison.Ordinal)
                    ? ChannelJson
                    : GlobalJson)));
        using StreamKeeprAppFactory factory = CreateFactory(
            handler, enabled: true, resolver: resolver);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string[] codes = json.RootElement.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("code").GetString()!)
            .ToArray();
        Assert.Equal(["SHARED", "CHANNEL", "KEKW"], codes);
        Assert.Contains(
            handler.Requests,
            request => request.RequestUri?.AbsoluteUri ==
                "https://api.betterttv.net/3/cached/users/twitch/12345");
        Assert.Equal(1, resolver.Lookups);
    }

    [Fact]
    public async Task An_unresolved_channel_returns_global_entries_only()
    {
        var resolver = new FakeEmoteChannelResolver();
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        using StreamKeeprAppFactory factory = CreateFactory(
            handler, enabled: true, resolver: resolver);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement entry = Assert.Single(
            json.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("KEKW", entry.GetProperty("code").GetString());
        Assert.Single(handler.Requests);
        Assert.Equal(1, resolver.Lookups);
    }

    [Fact]
    public async Task A_disabled_feature_does_not_resolve_the_channel()
    {
        var resolver = new FakeEmoteChannelResolver
        {
            Resolve = (_, _) => throw new InvalidOperationException("must not resolve"),
        };
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        using StreamKeeprAppFactory factory = CreateFactory(
            handler, enabled: false, resolver: resolver);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, resolver.Lookups);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_channel_resolution_is_cached_across_requests()
    {
        var poller = new ScriptedStreamPoller
        {
            Lookup = (_, _) => Task.FromResult<ChannelProfile?>(
                new ChannelProfile("12345", "chan", null)),
        };
        var handler = new StubHttpMessageHandler((request, _) =>
            Task.FromResult(EmoteTestData.Json(
                request.RequestUri!.AbsolutePath.Contains("/users/twitch/", StringComparison.Ordinal)
                    ? ChannelJson
                    : GlobalJson)));
        using StreamKeeprAppFactory factory = CreateFactory(
            handler, enabled: true, poller: poller);
        using HttpClient client = factory.CreateClient();

        await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");
        using HttpResponseMessage second =
            await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(
            3,
            json.RootElement.GetProperty("entries").GetArrayLength());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, poller.Lookups);
    }

    [Fact]
    public async Task A_channel_failure_still_returns_the_global_catalog()
    {
        var resolver = new FakeEmoteChannelResolver("12345");
        var handler = new StubHttpMessageHandler((request, _) =>
            Task.FromResult(
                request.RequestUri!.AbsolutePath.Contains("/users/twitch/", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : EmoteTestData.Json(GlobalJson)));
        using StreamKeeprAppFactory factory = CreateFactory(
            handler, enabled: true, resolver: resolver);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = json.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        JsonElement entry = Assert.Single(root.GetProperty("entries").EnumerateArray());
        Assert.Equal("KEKW", entry.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_global_failure_still_returns_the_channel_catalog()
    {
        var resolver = new FakeEmoteChannelResolver("12345");
        var handler = new StubHttpMessageHandler((request, _) =>
            Task.FromResult(
                request.RequestUri!.AbsolutePath.Contains("/users/twitch/", StringComparison.Ordinal)
                    ? EmoteTestData.Json(ChannelJson)
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using StreamKeeprAppFactory factory = CreateFactory(
            handler, enabled: true, resolver: resolver);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = json.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        string[] codes = root.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("code").GetString()!)
            .ToArray();
        Assert.Equal(["SHARED", "CHANNEL"], codes);
    }

    [Fact]
    public async Task Seven_tv_entries_follow_betterttv_entries_with_their_own_image_urls()
    {
        var handler = new StubHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.Host == "api.7tv.app"
                ? EmoteTestData.Json(SevenTvGlobalJson)
                : EmoteTestData.Json(GlobalJson)));
        using StreamKeeprAppFactory factory = CreateFactory(
            handler, enabled: true, sevenTv: true);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement[] entries = json.RootElement.GetProperty("entries")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(["bttv", "7tv"], entries.Select(entry => entry.GetProperty("provider").GetString()));
        Assert.Equal(["KEKW", "ppL"], entries.Select(entry => entry.GetProperty("code").GetString()));
        Assert.Equal(
            "https://cdn.7tv.app/emote/01GGD5PJA8000FH13S498E9D8X/1x.webp",
            entries[1].GetProperty("src").GetString());
    }

    [Fact]
    public async Task A_disabled_seven_tv_provider_makes_no_request()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        using StreamKeeprAppFactory factory = CreateFactory(handler, enabled: true);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(
            handler.Requests,
            request => request.RequestUri?.Host == "api.7tv.app");

        // The disabled provider contributes nothing: the envelope is the BetterTTV-only one.
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement entry = Assert.Single(json.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("bttv", entry.GetProperty("provider").GetString());
        Assert.Equal("KEKW", entry.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_seven_tv_channel_404_still_serves_its_global_catalog()
    {
        var handler = new StubHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.Contains("/users/twitch/", StringComparison.Ordinal)
                ? EmoteTestData.Json(HttpStatusCode.NotFound, """{"error_code":12000}""")
                : EmoteTestData.Json(SevenTvGlobalJson)));
        using StreamKeeprAppFactory factory = CreateFactory(
            handler, enabled: false, sevenTv: true, resolver: new FakeEmoteChannelResolver("71092938"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = json.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        JsonElement entry = Assert.Single(root.GetProperty("entries").EnumerateArray());
        Assert.Equal("7tv", entry.GetProperty("provider").GetString());
        Assert.Equal("ppL", entry.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_seven_tv_channel_entry_renders_through_the_envelope()
    {
        var handler = new StubHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.Contains("/users/twitch/", StringComparison.Ordinal)
                ? EmoteTestData.Json(SevenTvChannelJson)
                : EmoteTestData.Json(SevenTvGlobalJson)));
        using StreamKeeprAppFactory factory = CreateFactory(
            handler, enabled: false, sevenTv: true, resolver: new FakeEmoteChannelResolver("71092938"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement[] entries = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(["CHANNELTV", "ppL"], entries.Select(entry => entry.GetProperty("code").GetString()));
        Assert.All(entries, entry => Assert.Equal("7tv", entry.GetProperty("provider").GetString()));
        Assert.Equal(
            "https://cdn.7tv.app/emote/01G3WEGZN0000ET2J0MQP5YJ0G/1x.webp",
            entries[0].GetProperty("src").GetString());
    }

    [Fact]
    public async Task An_unresolved_channel_still_serves_the_global_seven_tv_catalog()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(SevenTvGlobalJson)));
        using StreamKeeprAppFactory factory = CreateFactory(
            handler, enabled: false, sevenTv: true, resolver: new FakeEmoteChannelResolver());
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement entry = Assert.Single(json.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("ppL", entry.GetProperty("code").GetString());
        Assert.DoesNotContain(
            handler.Requests,
            request => request.RequestUri!.AbsolutePath.Contains("/users/twitch/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_seven_tv_failure_leaves_the_endpoint_and_health_healthy()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using StreamKeeprAppFactory factory = CreateFactory(
            handler, enabled: false, sevenTv: true, resolver: new FakeEmoteChannelResolver("71092938"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/recordings/{Summary.Id}/emotes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("available").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("entries").EnumerateArray());

        using HttpResponseMessage health = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private static StreamKeeprAppFactory CreateFactory(
        HttpMessageHandler handler,
        bool enabled,
        TimeProvider? clock = null,
        FakeEmoteChannelResolver? resolver = null,
        ScriptedStreamPoller? poller = null,
        bool sevenTv = false)
    {
        var overrides = new Dictionary<string, string?>
        {
            [$"{ChatOptions.SectionName}:Emotes:Bttv:Enabled"] = enabled ? "true" : "false",
            [$"{ChatOptions.SectionName}:Emotes:SevenTv:Enabled"] = sevenTv ? "true" : "false",
        };

        var factory = new StreamKeeprAppFactory(
            overrides,
            configureTestServices: services =>
            {
                services.AddHttpClient(EmoteHttpClients.Bttv)
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
                services.AddHttpClient(EmoteHttpClients.SevenTv)
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
                if (poller is not null)
                {
                    // Exercise the real resolver and its cache with a scripted Helix lookup.
                    services.AddSingleton<IStreamPoller>(poller);
                    services.AddSingleton<IEmoteChannelResolver>(
                        provider => provider.GetRequiredService<EmoteChannelResolver>());
                }
                else
                {
                    services.AddSingleton<IEmoteChannelResolver>(
                        resolver ?? new FakeEmoteChannelResolver());
                }

                if (clock is not null)
                    services.AddSingleton(clock);
            });

        factory.Services.GetRequiredService<RecordingLibrary>().Upsert(Summary);
        return factory;
    }
}
