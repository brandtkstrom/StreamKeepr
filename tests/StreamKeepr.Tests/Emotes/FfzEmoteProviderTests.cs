using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Emotes;

namespace StreamKeepr.Tests.Emotes;

public sealed class FfzEmoteProviderTests
{
    // KEKW is public:false but renderable; YooHoo advertises only 1x/2x; JamsBunny is animated;
    // ZrehplaR is a hidden easter egg; the 1539687 set is modifier-only.
    private const string GlobalJson = """
        {
          "default_sets": [3, 1539687],
          "sets": {
            "3": {
              "id": 3,
              "title": "Global Emotes",
              "emoticons": [
                {
                  "id": 381875,
                  "name": "KEKW",
                  "public": false,
                  "hidden": false,
                  "modifier": false,
                  "urls": {
                    "1": "https://cdn.frankerfacez.com/emote/381875/1",
                    "2": "https://cdn.frankerfacez.com/emote/381875/2",
                    "4": "https://cdn.frankerfacez.com/emote/381875/4"
                  }
                },
                {
                  "id": 6,
                  "name": "YooHoo",
                  "public": false,
                  "hidden": false,
                  "modifier": false,
                  "urls": {
                    "1": "https://cdn.frankerfacez.com/emote/6/1",
                    "2": "https://cdn.frankerfacez.com/emote/6/2"
                  }
                },
                {
                  "id": 780274,
                  "name": "JamsBunny",
                  "public": true,
                  "hidden": false,
                  "modifier": false,
                  "urls": {
                    "1": "https://cdn.frankerfacez.com/emote/780274/1",
                    "2": "https://cdn.frankerfacez.com/emote/780274/2",
                    "4": "https://cdn.frankerfacez.com/emote/780274/4"
                  },
                  "animated": {
                    "1": "https://cdn.frankerfacez.com/emote/780274/animated/1",
                    "2": "https://cdn.frankerfacez.com/emote/780274/animated/2",
                    "4": "https://cdn.frankerfacez.com/emote/780274/animated/4"
                  }
                },
                {
                  "id": 9,
                  "name": "ZrehplaR",
                  "public": false,
                  "hidden": true,
                  "modifier": false,
                  "urls": { "1": "https://cdn.frankerfacez.com/emote/9/1" }
                }
              ]
            },
            "1532818": {
              "id": 1532818,
              "title": "Subwoofer Emote Effects",
              "emoticons": [
                { "id": 723891, "name": "subwoofer", "modifier": true, "modifier_flags": 1 }
              ]
            },
            "1539687": {
              "id": 1539687,
              "title": "Emote Effects",
              "emoticons": [
                { "id": 723890, "name": "ffzW", "modifier": true, "modifier_flags": 9 }
              ]
            }
          },
          "users": { "1532818": [123456] }
        }
        """;

    [Fact]
    public async Task Global_catalog_reads_default_sets_and_skips_hidden_effects_and_non_default_sets()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        FfzEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        Assert.Equal(["KEKW", "YooHoo", "JamsBunny"], entries.Select(entry => entry.Code));
        Assert.Equal(["381875", "6", "780274"], entries.Select(entry => entry.Id));
        Assert.False(entries[0].Animated);
        Assert.False(entries[1].Animated);
        Assert.True(entries[2].Animated);

        CapturedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "https://api.frankerfacez.com/v1/set/global",
            request.RequestUri?.AbsoluteUri);
        Assert.DoesNotContain(
            request.Headers,
            header => header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            request.Headers,
            header => header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("""{"default_sets":[],"sets":{}}""")]
    [InlineData("""{"sets":{}}""")]
    public async Task An_empty_or_missing_default_set_list_is_an_empty_catalog(string json)
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(json)));
        FfzEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task A_default_set_missing_from_sets_is_skipped()
    {
        const string json = """
            {
              "default_sets": [3, 999],
              "sets": {
                "3": {
                  "emoticons": [
                    { "id": 381875, "name": "KEKW", "urls": { "1": "https://cdn.frankerfacez.com/emote/381875/1" } }
                  ]
                }
              }
            }
            """;
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(json)));
        FfzEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        Assert.Equal(["KEKW"], entries.Select(entry => entry.Code));
    }

    [Fact]
    public async Task Global_catalog_uses_only_the_sizes_the_payload_advertises()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        FfzEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        EmoteImageSet full = provider.GetImageSet(entries[0]);
        Assert.Equal("https://cdn.frankerfacez.com/emote/381875/1", full.Src);
        Assert.Equal(
            "https://cdn.frankerfacez.com/emote/381875/1 1x, " +
            "https://cdn.frankerfacez.com/emote/381875/2 2x, " +
            "https://cdn.frankerfacez.com/emote/381875/4 4x",
            full.SrcSet);

        EmoteImageSet shortSet = provider.GetImageSet(entries[1]);
        Assert.Equal("https://cdn.frankerfacez.com/emote/6/1", shortSet.Src);
        Assert.Equal(
            "https://cdn.frankerfacez.com/emote/6/1 1x, " +
            "https://cdn.frankerfacez.com/emote/6/2 2x",
            shortSet.SrcSet);
        Assert.DoesNotContain("3x", shortSet.SrcSet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_animated_entry_emits_only_sizes_both_maps_advertise()
    {
        const string json = """
            {
              "default_sets": [3],
              "sets": {
                "3": {
                  "emoticons": [
                    {
                      "id": 780274,
                      "name": "JamsBunny",
                      "urls": { "1": "u1", "2": "u2", "4": "u4" },
                      "animated": { "1": "a1", "2": "a2" }
                    }
                  ]
                }
              }
            }
            """;
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(json)));
        FfzEmoteProvider provider = CreateProvider(handler, animated: true);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        EmoteImageSet image = provider.GetImageSet(Assert.Single(entries));
        Assert.Equal("https://cdn.frankerfacez.com/emote/780274/animated/1", image.Src);
        Assert.Equal(
            "https://cdn.frankerfacez.com/emote/780274/animated/1 1x, " +
            "https://cdn.frankerfacez.com/emote/780274/animated/2 2x",
            image.SrcSet);
        Assert.DoesNotContain("4x", image.SrcSet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Animated_entries_use_the_static_urls_by_default_and_the_animated_urls_when_enabled()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        FfzEmoteProvider staticProvider = CreateProvider(handler);
        IReadOnlyList<EmoteCatalogEntry> entries =
            await staticProvider.GetGlobalAsync(CancellationToken.None);

        Assert.Equal(
            "https://cdn.frankerfacez.com/emote/780274/1",
            staticProvider.GetImageSet(entries[2]).Src);

        FfzEmoteProvider animatedProvider = CreateProvider(handler, animated: true);
        EmoteImageSet animated = animatedProvider.GetImageSet(entries[2]);
        Assert.Equal("https://cdn.frankerfacez.com/emote/780274/animated/1", animated.Src);
        Assert.Equal(
            "https://cdn.frankerfacez.com/emote/780274/animated/1 1x, " +
            "https://cdn.frankerfacez.com/emote/780274/animated/2 2x, " +
            "https://cdn.frankerfacez.com/emote/780274/animated/4 4x",
            animated.SrcSet);
    }

    [Fact]
    public async Task Global_catalog_drops_entries_with_invalid_shapes()
    {
        string json = JsonSerializer.Serialize(new
        {
            default_sets = new[] { 3 },
            sets = new Dictionary<string, object>
            {
                ["3"] = new
                {
                    emoticons = new object[]
                    {
                        new { id = 381875, name = "KEEP", hidden = false, modifier = false, urls = new Dictionary<string, string> { ["1"] = "x" } },
                        new { id = 0, name = "ZERO", hidden = false, modifier = false, urls = new Dictionary<string, string> { ["1"] = "x" } },
                        new { id = -1, name = "NEGATIVE", hidden = false, modifier = false, urls = new Dictionary<string, string> { ["1"] = "x" } },
                        new { id = 381875.5m, name = "FRACTIONAL", hidden = false, modifier = false, urls = new Dictionary<string, string> { ["1"] = "x" } },
                        new { id = 123456789012345678901m, name = "TOOLARGE", hidden = false, modifier = false, urls = new Dictionary<string, string> { ["1"] = "x" } },
                        new { id = "381875", name = "STRINGID", hidden = false, modifier = false, urls = new Dictionary<string, string> { ["1"] = "x" } },
                        new { id = 381876, name = "", hidden = false, modifier = false, urls = new Dictionary<string, string> { ["1"] = "x" } },
                        new { id = 381877, name = new string('a', 129), hidden = false, modifier = false, urls = new Dictionary<string, string> { ["1"] = "x" } },
                        new { id = 381878, name = "MODIFIER", hidden = false, modifier = true, urls = new Dictionary<string, string> { ["1"] = "x" } },
                        new { id = 381879, name = "HIDDEN", hidden = true, modifier = false, urls = new Dictionary<string, string> { ["1"] = "x" } },
                        new { id = 381880, name = "NOURLS", hidden = false, modifier = false },
                        new { id = 381881, name = "NOONEX", hidden = false, modifier = false, urls = new Dictionary<string, string> { ["2"] = "x" } },
                    },
                },
            },
        });

        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(json)));
        FfzEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        Assert.Equal(["KEEP"], entries.Select(entry => entry.Code));
    }

    [Fact]
    public async Task Global_catalog_reports_a_non_success_http_status()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        FfzEmoteProvider provider = CreateProvider(handler);

        EmoteProviderException error = await Assert.ThrowsAsync<EmoteProviderException>(() =>
            provider.GetGlobalAsync(CancellationToken.None));

        Assert.Equal("ffz", error.ProviderId);
        Assert.Equal(503, error.HttpStatus);
        Assert.Equal(EmoteProviderFailureKind.HttpStatus, error.Kind);
    }

    [Fact]
    public async Task Global_catalog_treats_a_404_as_a_provider_failure()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        FfzEmoteProvider provider = CreateProvider(handler);

        EmoteProviderException error = await Assert.ThrowsAsync<EmoteProviderException>(() =>
            provider.GetGlobalAsync(CancellationToken.None));

        Assert.Equal("ffz", error.ProviderId);
        Assert.Equal(404, error.HttpStatus);
        Assert.Equal(EmoteProviderFailureKind.HttpStatus, error.Kind);
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
        FfzEmoteProvider provider = CreateProvider(handler);

        EmoteProviderException error = await Assert.ThrowsAsync<EmoteProviderException>(() =>
            provider.GetGlobalAsync(CancellationToken.None));

        Assert.Equal("ffz", error.ProviderId);
        Assert.Null(error.HttpStatus);
        Assert.Equal(EmoteProviderFailureKind.TooLarge, error.Kind);
    }

    [Fact]
    public async Task Global_catalog_treats_malformed_json_as_a_provider_failure()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json("{not json")));
        FfzEmoteProvider provider = CreateProvider(handler);

        EmoteProviderException error = await Assert.ThrowsAsync<EmoteProviderException>(() =>
            provider.GetGlobalAsync(CancellationToken.None));

        Assert.Equal("ffz", error.ProviderId);
        Assert.Null(error.HttpStatus);
        Assert.Equal(EmoteProviderFailureKind.InvalidPayload, error.Kind);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Provider_enablement_follows_the_ffz_option(bool configured, bool expected)
    {
        FfzEmoteProvider provider = CreateProvider(
            new StubHttpMessageHandler((_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson))),
            enabled: configured);

        Assert.Equal(expected, provider.Enabled);
    }

    private const string ChannelJson = """
        {
          "room": {
            "_id": 166895,
            "twitch_id": 71092938,
            "id": "xqc",
            "display_name": "xQc",
            "is_group": false,
            "set": 166907
          },
          "sets": {
            "166907": {
              "id": 166907,
              "title": "Channel: xQc",
              "emoticons": [
                {
                  "id": 246878,
                  "name": "WideHard",
                  "public": true,
                  "hidden": false,
                  "modifier": false,
                  "urls": {
                    "1": "https://cdn.frankerfacez.com/emote/246878/1",
                    "2": "https://cdn.frankerfacez.com/emote/246878/2",
                    "4": "https://cdn.frankerfacez.com/emote/246878/4"
                  }
                },
                {
                  "id": 407009,
                  "name": "KEKL",
                  "public": true,
                  "hidden": false,
                  "modifier": false,
                  "urls": {
                    "1": "https://cdn.frankerfacez.com/emote/407009/1",
                    "2": "https://cdn.frankerfacez.com/emote/407009/2",
                    "4": "https://cdn.frankerfacez.com/emote/407009/4"
                  }
                }
              ]
            },
            "166908": {
              "id": 166908,
              "title": "Second Set",
              "emoticons": [
                {
                  "id": 128054,
                  "name": "OMEGALUL",
                  "public": true,
                  "hidden": false,
                  "modifier": false,
                  "urls": {
                    "1": "https://cdn.frankerfacez.com/emote/128054/1",
                    "2": "https://cdn.frankerfacez.com/emote/128054/2",
                    "4": "https://cdn.frankerfacez.com/emote/128054/4"
                  }
                },
                { "id": 723890, "name": "ffzW", "modifier": true }
              ]
            }
          }
        }
        """;

    [Fact]
    public async Task Channel_catalog_reads_every_room_set_and_drops_modifiers()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(ChannelJson)));
        FfzEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetChannelAsync("71092938", CancellationToken.None);

        Assert.Equal(["WideHard", "KEKL", "OMEGALUL"], entries.Select(entry => entry.Code));
        Assert.Equal(["246878", "407009", "128054"], entries.Select(entry => entry.Id));

        CapturedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://api.frankerfacez.com/v1/room/id/71092938",
            request.RequestUri?.AbsoluteUri);
        Assert.DoesNotContain(
            request.Headers,
            header => header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            request.Headers,
            header => header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_unknown_room_is_an_empty_channel_catalog()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            EmoteTestData.Json(
                HttpStatusCode.NotFound,
                """{"status":404,"error":"Not Found","message":"No such room"}""")));
        FfzEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetChannelAsync("123456", CancellationToken.None);

        Assert.Empty(entries);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Channel_catalog_reports_a_non_success_http_status()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        FfzEmoteProvider provider = CreateProvider(handler);

        EmoteProviderException error = await Assert.ThrowsAsync<EmoteProviderException>(() =>
            provider.GetChannelAsync("71092938", CancellationToken.None));

        Assert.Equal("ffz", error.ProviderId);
        Assert.Equal(500, error.HttpStatus);
        Assert.Equal(EmoteProviderFailureKind.HttpStatus, error.Kind);
    }

    [Fact]
    public async Task Channel_catalog_treats_malformed_json_as_a_provider_failure()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json("{not json")));
        FfzEmoteProvider provider = CreateProvider(handler);

        EmoteProviderException error = await Assert.ThrowsAsync<EmoteProviderException>(() =>
            provider.GetChannelAsync("71092938", CancellationToken.None));

        Assert.Equal("ffz", error.ProviderId);
        Assert.Null(error.HttpStatus);
        Assert.Equal(EmoteProviderFailureKind.InvalidPayload, error.Kind);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("123456789012345678901")]
    public async Task Get_channel_rejects_a_non_numeric_id_without_a_request(string channelId)
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        FfzEmoteProvider provider = CreateProvider(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.GetChannelAsync(channelId, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    private static FfzEmoteProvider CreateProvider(
        HttpMessageHandler handler, bool enabled = true, bool animated = false) =>
        new(
            new EmoteHttpClientFactory(handler),
            Options.Create(new ChatOptions
            {
                Emotes = new ChatEmoteOptions
                {
                    Ffz = new EmoteProviderOptions { Enabled = enabled },
                    Animated = animated,
                },
            }));
}
