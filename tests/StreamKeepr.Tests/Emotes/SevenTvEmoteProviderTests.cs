using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Emotes;

namespace StreamKeepr.Tests.Emotes;

public sealed class SevenTvEmoteProviderTests
{
    // RainTime is zero-width (set-entry flags 1), ppL is static, and (7TV) is animated and
    // renamed: its set-entry name differs from the nested emote name.
    private const string GlobalJson = """
        {
          "id": "01HKQT8EWR000ESSWF3625XCS4",
          "name": "Global Emotes",
          "flags": 0,
          "emotes": [
            {
              "id": "01FCY771D800007PQ2DF3GDTN6",
              "name": "RainTime",
              "flags": 1,
              "data": { "id": "01FCY771D800007PQ2DF3GDTN6", "name": "RainTime", "flags": 256, "animated": true }
            },
            {
              "id": "01GGD5PJA8000FH13S498E9D8X",
              "name": "ppL",
              "flags": 0,
              "data": { "id": "01GGD5PJA8000FH13S498E9D8X", "name": "ppL", "flags": 0, "animated": false, "state": ["PERSONAL", "NO_PERSONAL"] }
            },
            {
              "id": "01GKQXGX1R000216YB0K7GN92X",
              "name": "(7TV)",
              "flags": 0,
              "data": { "id": "01GKQXGX1R000216YB0K7GN92X", "name": "7tvM", "flags": 256, "animated": true }
            }
          ]
        }
        """;

    private const string StaticId = "01GGD5PJA8000FH13S498E9D8X";
    private const string AnimatedId = "01GKQXGX1R000216YB0K7GN92X";

    [Fact]
    public async Task Global_catalog_uses_the_set_entry_name_and_drops_zero_width_entries()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        SevenTvEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        Assert.Collection(
            entries,
            entry =>
            {
                Assert.Equal(StaticId, entry.Id);
                Assert.Equal("ppL", entry.Code);
                Assert.False(entry.Animated);
            },
            entry =>
            {
                Assert.Equal(AnimatedId, entry.Id);
                Assert.Equal("(7TV)", entry.Code);
                Assert.True(entry.Animated);
            });

        CapturedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "https://api.7tv.app/v3/emote-sets/global",
            request.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task Static_images_use_the_plain_name_and_animated_images_use_the_static_suffix()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        SevenTvEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        EmoteImageSet still = provider.GetImageSet(entries[0]);
        Assert.Equal($"https://cdn.7tv.app/emote/{StaticId}/1x.webp", still.Src);
        Assert.Equal(
            $"https://cdn.7tv.app/emote/{StaticId}/1x.webp 1x, " +
            $"https://cdn.7tv.app/emote/{StaticId}/2x.webp 2x, " +
            $"https://cdn.7tv.app/emote/{StaticId}/3x.webp 3x",
            still.SrcSet);

        EmoteImageSet animated = provider.GetImageSet(entries[1]);
        Assert.Equal($"https://cdn.7tv.app/emote/{AnimatedId}/1x_static.webp", animated.Src);
        Assert.Equal(
            $"https://cdn.7tv.app/emote/{AnimatedId}/1x_static.webp 1x, " +
            $"https://cdn.7tv.app/emote/{AnimatedId}/2x_static.webp 2x, " +
            $"https://cdn.7tv.app/emote/{AnimatedId}/3x_static.webp 3x",
            animated.SrcSet);
    }

    [Fact]
    public async Task Global_catalog_drops_entries_with_the_wrong_identifier_shape()
    {
        string json = JsonSerializer.Serialize(new
        {
            emotes = new object[]
            {
                new { id = StaticId, name = "KEEP", flags = 0, data = new { animated = false } },
                // A BetterTTV-shaped 24-hex ID must not pass the 26-character 7TV rule.
                new { id = "54fa8f1401e468494b85b537", name = "HEXID", flags = 0, data = new { animated = false } },
                new { id = "01ggd5pja8000fh13s498e9d8x", name = "LOWERCASE", flags = 0, data = new { animated = false } },
                new { id = "01GGD5PJA8000FH13S498E9D8", name = "SHORT", flags = 0, data = new { animated = false } },
                new { id = "01GGD5PJA8000FH13S498E9D8XX", name = "LONG", flags = 0, data = new { animated = false } },
                new { id = StaticId, name = "", flags = 0, data = new { animated = false } },
                new { id = StaticId, name = new string('a', 129), flags = 0, data = new { animated = false } },
            },
        });

        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(json)));
        SevenTvEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        Assert.Equal(["KEEP"], entries.Select(entry => entry.Code));
    }

    private const string ChannelJson = """
        {
          "id": "71092938",
          "platform": "TWITCH",
          "username": "xqc",
          "emote_set_id": "01FE9DRF000009TR6M9N941CYW",
          "emote_set": {
            "id": "01FE9DRF000009TR6M9N941CYW",
            "name": "xQc's Emotes",
            "flags": 0,
            "emotes": [
              { "id": "01G3WEGZN0000ET2J0MQP5YJ0G", "name": "GAMBA", "flags": 0, "data": { "name": "GAMBA", "flags": 256, "animated": true } },
              { "id": "01G7YR9X5G0003Z50SB3FM5WR4", "name": "Happi", "flags": 0, "data": { "name": "jupijej", "flags": 256, "animated": true } },
              { "id": "01GCH1VX300004KGVJA52HCNPS", "name": "PutAFingerDown", "flags": 0, "data": { "name": "Me:", "flags": 0, "animated": false } }
            ]
          }
        }
        """;

    [Fact]
    public async Task Channel_catalog_reads_the_inline_set_in_payload_order()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(ChannelJson)));
        SevenTvEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetChannelAsync("71092938", CancellationToken.None);

        Assert.Equal(
            ["GAMBA", "Happi", "PutAFingerDown"],
            entries.Select(entry => entry.Code));
        Assert.True(entries[0].Animated);
        Assert.False(entries[2].Animated);

        CapturedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://api.7tv.app/v3/users/twitch/71092938",
            request.RequestUri?.AbsoluteUri);
        Assert.DoesNotContain(
            request.Headers,
            header => header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            request.Headers,
            header => header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_channel_payload_without_a_set_follows_the_set_id()
    {
        const string userJson = """
            { "id": "71092938", "emote_set_id": "01FE9DRF000009TR6M9N941CYW", "emote_set": null }
            """;
        const string setJson = """
            {
              "id": "01FE9DRF000009TR6M9N941CYW",
              "name": "xQc's Emotes",
              "emotes": [
                { "id": "01G3WEGZN0000ET2J0MQP5YJ0G", "name": "GAMBA", "flags": 0, "data": { "animated": true } }
              ]
            }
            """;
        var handler = new StubHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.Contains("/users/twitch/", StringComparison.Ordinal)
                ? EmoteTestData.Json(userJson)
                : EmoteTestData.Json(setJson)));
        SevenTvEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetChannelAsync("71092938", CancellationToken.None);

        Assert.Equal(["GAMBA"], entries.Select(entry => entry.Code));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            "https://api.7tv.app/v3/emote-sets/01FE9DRF000009TR6M9N941CYW",
            handler.Requests[1].RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task A_channel_payload_without_a_set_or_a_set_id_is_an_empty_catalog()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            EmoteTestData.Json("""{ "id": "71092938", "emote_set_id": null, "emote_set": null }""")));
        SevenTvEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetChannelAsync("71092938", CancellationToken.None);

        Assert.Empty(entries);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_set_id_outside_the_identifier_shape_never_becomes_a_request()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            EmoteTestData.Json("""{ "id": "71092938", "emote_set_id": "../../admin", "emote_set": null }""")));
        SevenTvEmoteProvider provider = CreateProvider(handler);

        EmoteProviderException error = await Assert.ThrowsAsync<EmoteProviderException>(() =>
            provider.GetChannelAsync("71092938", CancellationToken.None));

        Assert.Equal("7tv", error.ProviderId);
        Assert.Equal(EmoteProviderFailureKind.InvalidPayload, error.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task An_unknown_channel_profile_is_an_empty_channel_catalog()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            EmoteTestData.Json(HttpStatusCode.NotFound, """{"error_code":12000,"error":"user not found"}""")));
        SevenTvEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetChannelAsync("1", CancellationToken.None);

        Assert.Empty(entries);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_removed_channel_set_is_an_empty_channel_catalog()
    {
        const string userJson = """
            { "id": "71092938", "emote_set_id": "01FE9DRF000009TR6M9N941CYW", "emote_set": null }
            """;
        var handler = new StubHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.Contains("/users/twitch/", StringComparison.Ordinal)
                ? EmoteTestData.Json(userJson)
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        SevenTvEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetChannelAsync("71092938", CancellationToken.None);

        Assert.Empty(entries);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_failed_set_follow_up_fails_the_channel_scope()
    {
        const string userJson = """
            { "id": "71092938", "emote_set_id": "01FE9DRF000009TR6M9N941CYW", "emote_set": null }
            """;
        var handler = new StubHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.Contains("/users/twitch/", StringComparison.Ordinal)
                ? EmoteTestData.Json(userJson)
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        SevenTvEmoteProvider provider = CreateProvider(handler);

        EmoteProviderException error = await Assert.ThrowsAsync<EmoteProviderException>(() =>
            provider.GetChannelAsync("71092938", CancellationToken.None));

        Assert.Equal("7tv", error.ProviderId);
        Assert.Equal(503, error.HttpStatus);
        Assert.Equal(EmoteProviderFailureKind.HttpStatus, error.Kind);
    }

    [Fact]
    public async Task Channel_catalog_reports_a_non_success_http_status()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        SevenTvEmoteProvider provider = CreateProvider(handler);

        EmoteProviderException error = await Assert.ThrowsAsync<EmoteProviderException>(() =>
            provider.GetChannelAsync("71092938", CancellationToken.None));

        Assert.Equal("7tv", error.ProviderId);
        Assert.Equal(500, error.HttpStatus);
        Assert.Equal(EmoteProviderFailureKind.HttpStatus, error.Kind);
    }

    [Fact]
    public async Task Global_catalog_treats_malformed_json_as_a_provider_failure()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json("{not json")));
        SevenTvEmoteProvider provider = CreateProvider(handler);

        EmoteProviderException error = await Assert.ThrowsAsync<EmoteProviderException>(() =>
            provider.GetGlobalAsync(CancellationToken.None));

        Assert.Equal("7tv", error.ProviderId);
        Assert.Null(error.HttpStatus);
        Assert.Equal(EmoteProviderFailureKind.InvalidPayload, error.Kind);
    }

    [Fact]
    public async Task Global_catalog_rejects_a_body_over_the_response_cap()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    new string('x', EmoteCatalogRules.MaxResponseBytes + 1),
                    Encoding.UTF8,
                    "application/json"),
            }));
        SevenTvEmoteProvider provider = CreateProvider(handler);

        EmoteProviderException error = await Assert.ThrowsAsync<EmoteProviderException>(() =>
            provider.GetGlobalAsync(CancellationToken.None));

        Assert.Equal("7tv", error.ProviderId);
        Assert.Null(error.HttpStatus);
        Assert.Equal(EmoteProviderFailureKind.TooLarge, error.Kind);
    }

    [Fact]
    public async Task A_three_mebibyte_body_is_accepted()
    {
        // The largest measured 7TV channel catalog is 2.38 MB; the shared cap has to admit it.
        const int padding = 3 * 1024 * 1024;
        string json = JsonSerializer.Serialize(new
        {
            pad = new string('x', padding),
            emotes = new object[]
            {
                new { id = StaticId, name = "ppL", flags = 0, data = new { animated = false } },
            },
        });

        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(json)));
        SevenTvEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        Assert.Equal(["ppL"], entries.Select(entry => entry.Code));
    }

    [Fact]
    public async Task A_three_mebibyte_channel_body_is_accepted()
    {
        const int padding = 3 * 1024 * 1024;
        string userJson = JsonSerializer.Serialize(new
        {
            id = "71092938",
            emote_set_id = "01FE9DRF000009TR6M9N941CYW",
            emote_set = new
            {
                pad = new string('x', padding),
                emotes = new object[]
                {
                    new { id = StaticId, name = "ppL", flags = 0, data = new { animated = false } },
                },
            },
        });

        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(userJson)));
        SevenTvEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetChannelAsync("71092938", CancellationToken.None);

        Assert.Equal(["ppL"], entries.Select(entry => entry.Code));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Provider_enablement_follows_the_seven_tv_option(bool configured, bool expected)
    {
        SevenTvEmoteProvider provider = CreateProvider(
            new StubHttpMessageHandler((_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson))),
            enabled: configured);

        Assert.Equal(expected, provider.Enabled);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("123456789012345678901")]
    public async Task Get_channel_rejects_a_non_numeric_id_without_a_request(string channelId)
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        SevenTvEmoteProvider provider = CreateProvider(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.GetChannelAsync(channelId, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Animated_entries_use_the_animated_asset_when_animation_is_enabled()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        SevenTvEmoteProvider provider = CreateProvider(handler, animated: true);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        Assert.Equal(
            $"https://cdn.7tv.app/emote/{AnimatedId}/1x.webp",
            provider.GetImageSet(entries[1]).Src);
        Assert.Equal(
            $"https://cdn.7tv.app/emote/{StaticId}/1x.webp",
            provider.GetImageSet(entries[0]).Src);
    }

    private static SevenTvEmoteProvider CreateProvider(
        HttpMessageHandler handler, bool enabled = true, bool animated = false) =>
        new(
            new EmoteHttpClientFactory(handler),
            Options.Create(new ChatOptions
            {
                Emotes = new ChatEmoteOptions
                {
                    SevenTv = new EmoteProviderOptions { Enabled = enabled },
                    Animated = animated,
                },
            }));
}
