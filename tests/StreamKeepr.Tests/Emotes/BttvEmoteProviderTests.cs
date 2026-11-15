using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Emotes;

namespace StreamKeepr.Tests.Emotes;

public sealed class BttvEmoteProviderTests
{
    private const string GlobalJson = """
        [
          {"id":"54fa8f1401e468494b85b537","code":":tf:","imageType":"png","animated":false,"modifier":false},
          {"id":"566ca38765dbbdab32ec0560","code":"KEKW","imageType":"gif","animated":true,"modifier":false}
        ]
        """;

    [Fact]
    public async Task Global_catalog_normalizes_entries_and_builds_static_image_urls()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        BttvEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        Assert.Collection(
            entries,
            entry =>
            {
                Assert.Equal(EmoteTestData.StaticId, entry.Id);
                Assert.Equal(":tf:", entry.Code);
                Assert.False(entry.Animated);

                EmoteImageSet image = provider.GetStaticImage(entry);
                Assert.Equal(
                    $"https://cdn.betterttv.net/emote/{EmoteTestData.StaticId}/1x.webp",
                    image.Src);
                Assert.Equal(
                    $"https://cdn.betterttv.net/emote/{EmoteTestData.StaticId}/1x.webp 1x, " +
                    $"https://cdn.betterttv.net/emote/{EmoteTestData.StaticId}/2x.webp 2x, " +
                    $"https://cdn.betterttv.net/emote/{EmoteTestData.StaticId}/3x.webp 3x",
                    image.SrcSet);
            },
            entry =>
            {
                Assert.Equal(EmoteTestData.AnimatedId, entry.Id);
                Assert.Equal("KEKW", entry.Code);
                Assert.True(entry.Animated);
            });

        CapturedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "https://api.betterttv.net/3/cached/emotes/global",
            request.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task Animated_entries_use_the_static_first_frame_urls()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        BttvEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        EmoteImageSet image = provider.GetStaticImage(entries[1]);
        Assert.Equal(
            $"https://cdn.betterttv.net/emote/{EmoteTestData.AnimatedId}/static/1x.webp",
            image.Src);
        Assert.Equal(
            $"https://cdn.betterttv.net/emote/{EmoteTestData.AnimatedId}/static/1x.webp 1x, " +
            $"https://cdn.betterttv.net/emote/{EmoteTestData.AnimatedId}/static/2x.webp 2x, " +
            $"https://cdn.betterttv.net/emote/{EmoteTestData.AnimatedId}/static/3x.webp 3x",
            image.SrcSet);
    }

    [Fact]
    public async Task Global_catalog_drops_invalid_modifier_and_restricted_entries()
    {
        string json = JsonSerializer.Serialize(new object[]
        {
            new { id = EmoteTestData.StaticId, code = ":tf:", animated = false, modifier = false },
            new { id = "not-an-emote-id", code = "BROKEN", animated = false, modifier = false },
            new { id = EmoteTestData.StaticId, code = "", animated = false, modifier = false },
            new { id = EmoteTestData.StaticId, code = new string('a', 129), animated = false, modifier = false },
            new { id = EmoteTestData.StaticId, code = "w!", animated = false, modifier = true },
            new
            {
                id = EmoteTestData.StaticId,
                code = "RESTRICTED",
                animated = false,
                modifier = false,
                restrictions = new { emoteSet = "night" },
            },
            new
            {
                id = EmoteTestData.StaticId,
                code = "DUUR",
                animated = false,
                modifier = false,
                codeOriginal = "FeelsTastyMan",
            },
        });

        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(json)));
        BttvEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        Assert.Collection(
            entries,
            entry => Assert.Equal(":tf:", entry.Code),
            entry => Assert.Equal("DUUR", entry.Code));
    }

    [Fact]
    public async Task Absent_and_empty_restrictions_do_not_drop_an_emote()
    {
        string json = JsonSerializer.Serialize(new object[]
        {
            new { id = EmoteTestData.StaticId, code = "ABSENT", animated = false, modifier = false },
            new
            {
                id = EmoteTestData.StaticId,
                code = "EMPTYARRAY",
                animated = false,
                modifier = false,
                restrictions = Array.Empty<object>(),
            },
            new
            {
                id = EmoteTestData.StaticId,
                code = "EMPTYOBJECT",
                animated = false,
                modifier = false,
                restrictions = new Dictionary<string, string>(),
            },
            new
            {
                id = EmoteTestData.StaticId,
                code = "RESTRICTED",
                animated = false,
                modifier = false,
                restrictions = new { emoteSet = "night" },
            },
        });

        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(json)));
        BttvEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        Assert.Equal(
            ["ABSENT", "EMPTYARRAY", "EMPTYOBJECT"],
            entries.Select(entry => entry.Code));
    }

    [Fact]
    public async Task Global_catalog_rejects_a_body_over_two_mebibytes()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    new string('x', EmoteCatalogRules.MaxResponseBytes + 1),
                    Encoding.UTF8,
                    "application/json"),
            }));
        BttvEmoteProvider provider = CreateProvider(handler);

        EmoteProviderException error = await Assert.ThrowsAsync<EmoteProviderException>(() =>
            provider.GetGlobalAsync(CancellationToken.None));

        Assert.Equal("bttv", error.ProviderId);
        Assert.Null(error.HttpStatus);
        Assert.Equal(EmoteProviderFailureKind.TooLarge, error.Kind);
    }

    [Fact]
    public async Task Global_catalog_reports_a_non_success_http_status()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        BttvEmoteProvider provider = CreateProvider(handler);

        EmoteProviderException error = await Assert.ThrowsAsync<EmoteProviderException>(() =>
            provider.GetGlobalAsync(CancellationToken.None));

        Assert.Equal("bttv", error.ProviderId);
        Assert.Equal(503, error.HttpStatus);
        Assert.Equal(EmoteProviderFailureKind.HttpStatus, error.Kind);
    }

    [Fact]
    public async Task Global_catalog_treats_malformed_json_as_a_provider_failure()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json("{not json")));
        BttvEmoteProvider provider = CreateProvider(handler);

        EmoteProviderException error = await Assert.ThrowsAsync<EmoteProviderException>(() =>
            provider.GetGlobalAsync(CancellationToken.None));

        Assert.Equal("bttv", error.ProviderId);
        Assert.Null(error.HttpStatus);
        Assert.Equal(EmoteProviderFailureKind.InvalidPayload, error.Kind);
    }

    [Fact]
    public async Task Global_catalog_request_is_credential_free()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        BttvEmoteProvider provider = CreateProvider(handler);

        await provider.GetGlobalAsync(CancellationToken.None);

        CapturedRequest request = Assert.Single(handler.Requests);
        Assert.DoesNotContain(
            request.Headers,
            header => header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            request.Headers,
            header => header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Provider_enablement_follows_the_bttv_option(bool configured, bool expected)
    {
        BttvEmoteProvider provider = CreateProvider(
            new StubHttpMessageHandler((_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson))),
            enabled: configured);

        Assert.Equal(expected, provider.Enabled);
    }

    private const string ChannelJson = """
        {
          "id": "5d1b7c0e95e8e11f4f6f0a1c",
          "displayName": "Chan",
          "channelEmotes": [
            {"id":"aaaaaaaaaaaaaaaaaaaaaaaa","code":"CHANNEL","animated":false,"modifier":false},
            {"id":"bbbbbbbbbbbbbbbbbbbbbbbb","code":"DUP","animated":false,"modifier":false},
            {"id":"cccccccccccccccccccccccc","code":"BRAZIL","animated":false,"modifier":false}
          ],
          "sharedEmotes": [
            {"id":"dddddddddddddddddddddddd","code":"SHARED","animated":true,"modifier":false},
            {"id":"eeeeeeeeeeeeeeeeeeeeeeee","code":"DUP","animated":false,"modifier":false},
            {"id":"ffffffffffffffffffffffff","code":"brazil","animated":false,"modifier":false}
          ]
        }
        """;

    [Fact]
    public async Task Channel_catalog_fetches_the_numeric_id_and_orders_shared_before_channel()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(ChannelJson)));
        BttvEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetChannelAsync("12345", CancellationToken.None);

        Assert.Equal(
            ["SHARED", "DUP", "brazil", "CHANNEL", "DUP", "BRAZIL"],
            entries.Select(entry => entry.Code));
        Assert.True(entries[0].Animated);

        CapturedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://api.betterttv.net/3/cached/users/twitch/12345",
            request.RequestUri?.AbsoluteUri);
        Assert.DoesNotContain(
            request.Headers,
            header => header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            request.Headers,
            header => header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Channel_catalog_drops_invalid_modifier_and_restricted_entries_and_matches_code()
    {
        string channelJson = JsonSerializer.Serialize(new
        {
            id = "5d1b7c0e95e8e11f4f6f0a1c",
            channelEmotes = new object[]
            {
                new { id = EmoteTestData.StaticId, code = "KEEP", animated = false, modifier = false },
                new { id = "not-an-emote-id", code = "BROKEN", animated = false, modifier = false },
                new { id = EmoteTestData.StaticId, code = "", animated = false, modifier = false },
            },
            sharedEmotes = new object[]
            {
                new { id = EmoteTestData.StaticId, code = "w!", animated = false, modifier = true },
                new
                {
                    id = EmoteTestData.StaticId,
                    code = "RESTRICTED",
                    animated = false,
                    modifier = false,
                    restrictions = new { emoteSet = "night" },
                },
                new
                {
                    id = EmoteTestData.StaticId,
                    code = "DUUR",
                    animated = false,
                    modifier = false,
                    codeOriginal = "FeelsTastyMan",
                },
            },
        });

        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(channelJson)));
        BttvEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetChannelAsync("12345", CancellationToken.None);

        Assert.Equal(["DUUR", "KEEP"], entries.Select(entry => entry.Code));
    }

    [Fact]
    public async Task An_unknown_channel_profile_is_an_empty_channel_catalog()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(EmoteTestData.Json(
            HttpStatusCode.NotFound, """{"message":"user not found"}""")));
        BttvEmoteProvider provider = CreateProvider(handler);

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetChannelAsync("999999", CancellationToken.None);

        Assert.Empty(entries);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Channel_catalog_reports_a_non_success_http_status()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        BttvEmoteProvider provider = CreateProvider(handler);

        EmoteProviderException error = await Assert.ThrowsAsync<EmoteProviderException>(() =>
            provider.GetChannelAsync("12345", CancellationToken.None));

        Assert.Equal("bttv", error.ProviderId);
        Assert.Equal(500, error.HttpStatus);
        Assert.Equal(EmoteProviderFailureKind.HttpStatus, error.Kind);
    }

    [Fact]
    public async Task Channel_catalog_treats_malformed_json_as_a_provider_failure()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json("{not json")));
        BttvEmoteProvider provider = CreateProvider(handler);

        EmoteProviderException error = await Assert.ThrowsAsync<EmoteProviderException>(() =>
            provider.GetChannelAsync("12345", CancellationToken.None));

        Assert.Equal("bttv", error.ProviderId);
        Assert.Null(error.HttpStatus);
        Assert.Equal(EmoteProviderFailureKind.InvalidPayload, error.Kind);
    }

    [Fact]
    public async Task Channel_catalog_rejects_a_body_over_two_mebibytes()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            EmoteTestData.Json(new string('x', EmoteCatalogRules.MaxResponseBytes + 1))));
        BttvEmoteProvider provider = CreateProvider(handler);

        EmoteProviderException error = await Assert.ThrowsAsync<EmoteProviderException>(() =>
            provider.GetChannelAsync("12345", CancellationToken.None));

        Assert.Equal("bttv", error.ProviderId);
        Assert.Null(error.HttpStatus);
        Assert.Equal(EmoteProviderFailureKind.TooLarge, error.Kind);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("123456789012345678901")]
    public async Task Get_channel_rejects_a_non_numeric_id_without_a_request(string channelId)
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(EmoteTestData.Json(GlobalJson)));
        BttvEmoteProvider provider = CreateProvider(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.GetChannelAsync(channelId, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    private static BttvEmoteProvider CreateProvider(
        HttpMessageHandler handler, bool enabled = true) =>
        new(
            new EmoteHttpClientFactory(handler),
            Options.Create(new ChatOptions
            {
                Emotes = new ChatEmoteOptions
                {
                    Bttv = new BttvEmoteOptions { Enabled = enabled },
                },
            }));
}
