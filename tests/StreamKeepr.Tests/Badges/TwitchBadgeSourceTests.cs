using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Badges;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Emotes;
using StreamKeepr.Tests.Twitch;

namespace StreamKeepr.Tests.Badges;

public sealed class TwitchBadgeSourceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

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
                  "description": "A moderator",
                  "click_action": "none"
                }
              ]
            },
            {
              "set_id": "verified",
              "versions": [
                {
                  "id": "1",
                  "image_url_1x": "https://static-cdn.jtvnw.net:443/badges/v1/verified/1",
                  "image_url_2x": "https://static-cdn.jtvnw.net:443/badges/v1/verified/2",
                  "image_url_4x": "https://static-cdn.jtvnw.net:443/badges/v1/verified/3",
                  "title": "Verified"
                }
              ]
            },
            { "set_id": "Bad Set", "versions": [] },
            {
              "set_id": "vip",
              "versions": [
                {
                  "id": "1.0",
                  "image_url_1x": "https://static-cdn.jtvnw.net/badges/v1/vip/1",
                  "image_url_2x": "https://static-cdn.jtvnw.net/badges/v1/vip/2",
                  "image_url_4x": "https://static-cdn.jtvnw.net/badges/v1/vip/3",
                  "title": "VIP"
                }
              ]
            },
            {
              "set_id": "subscriber",
              "versions": [
                {
                  "id": "0",
                  "image_url_1x": "http://static-cdn.jtvnw.net/badges/v1/sub/1",
                  "image_url_2x": "https://static-cdn.jtvnw.net/badges/v1/sub/2",
                  "image_url_4x": "https://static-cdn.jtvnw.net/badges/v1/sub/3",
                  "title": "Subscriber"
                }
              ]
            },
            {
              "set_id": "partner",
              "versions": [
                {
                  "id": "1",
                  "image_url_1x": "https://evil.example/badges/v1/partner/1",
                  "image_url_2x": "https://evil.example/badges/v1/partner/2",
                  "image_url_4x": "https://evil.example/badges/v1/partner/3",
                  "title": "Partner"
                }
              ]
            },
            {
              "set_id": "premium",
              "versions": [
                {
                  "id": "1",
                  "image_url_1x": "https://static-cdn.jtvnw.net:8443/badges/v1/premium/1",
                  "image_url_2x": "https://static-cdn.jtvnw.net:8443/badges/v1/premium/2",
                  "image_url_4x": "https://static-cdn.jtvnw.net:8443/badges/v1/premium/3",
                  "title": "Premium"
                }
              ]
            },
            { "set_id": "turbo", "versions": null },
            {
              "set_id": "staff",
              "versions": [
                {
                  "id": "1",
                  "image_url_1x": "https://static-cdn.jtvnw.net/badges/v1/staff/1",
                  "image_url_2x": "https://static-cdn.jtvnw.net/badges/v1/staff/2",
                  "image_url_4x": "https://static-cdn.jtvnw.net/badges/v1/staff/3",
                  "title": null
                }
              ]
            },
            {
              "set_id": "broadcaster",
              "versions": [
                {
                  "id": "1",
                  "image_url_1x": "https://static-cdn.jtvnw.net/badges/v1/broadcaster/1",
                  "image_url_2x": "https://static-cdn.jtvnw.net/badges/v1/broadcaster/2",
                  "image_url_4x": "https://static-cdn.jtvnw.net/badges/v1/broadcaster/3",
                  "title": "Broadcaster"
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task Requests_helix_with_the_client_id_and_an_app_access_token()
    {
        var handler = Handler(_ => Task.FromResult(EmoteTestData.Json(CatalogJson)));
        TwitchBadgeSource source = CreateSource(handler);

        await source.GetGlobalAsync(CancellationToken.None);

        CapturedRequest badge = Assert.Single(
            handler.Requests,
            request => request.RequestUri?.Host == "api.twitch.tv");
        Assert.Equal(
            "https://api.twitch.tv/helix/chat/badges/global",
            badge.RequestUri!.AbsoluteUri);
        Assert.Contains(
            badge.Headers,
            header => header.Key == "Client-Id" && header.Value == "test-client-id");
        Assert.Contains(
            badge.Headers,
            header => header.Key == "Authorization" && header.Value == "Bearer one");
    }

    [Fact]
    public async Task Normalizes_sets_and_versions_and_drops_untrusted_images()
    {
        var handler = Handler(_ => Task.FromResult(EmoteTestData.Json(CatalogJson)));
        IReadOnlyList<BadgeCatalogEntry> entries =
            await CreateSource(handler).GetGlobalAsync(CancellationToken.None);

        Assert.Equal(
            ["moderator/1", "verified/1", "staff/1", "broadcaster/1"],
            entries.Select(entry => $"{entry.Set}/{entry.Version}"));
        BadgeCatalogEntry moderator = entries[0];
        Assert.Equal("Moderator", moderator.Title);
        Assert.Equal(
            "https://static-cdn.jtvnw.net/badges/v1/moderator/1",
            moderator.Src);
        Assert.Equal(
            "https://static-cdn.jtvnw.net/badges/v1/moderator/1 1x, " +
            "https://static-cdn.jtvnw.net/badges/v1/moderator/2 2x, " +
            "https://static-cdn.jtvnw.net/badges/v1/moderator/3 4x",
            moderator.SrcSet);

        // A missing title falls back to the set name; the description and click data are ignored.
        Assert.Equal("Verified", entries[1].Title);
        Assert.Equal("staff", entries[2].Title);
    }

    [Fact]
    public async Task Invalid_sets_do_not_consume_the_set_cap()
    {
        string invalid = """{"set_id":"Bad Set","versions":[]}""";
        string json = $$"""{"data":[{{string.Join(",", Enumerable.Repeat(invalid, 500))}},{{Set("moderator", 1)}}]}""";
        var handler = Handler(_ => Task.FromResult(EmoteTestData.Json(json)));

        IReadOnlyList<BadgeCatalogEntry> entries =
            await CreateSource(handler).GetGlobalAsync(CancellationToken.None);

        BadgeCatalogEntry entry = Assert.Single(entries);
        Assert.Equal("moderator/v0", $"{entry.Set}/{entry.Version}");
    }

    [Fact]
    public async Task Invalid_versions_do_not_consume_the_version_cap()
    {
        string invalid = """{"id":"BAD ID","image_url_1x":"https://static-cdn.jtvnw.net/badges/v1/bad/1","image_url_2x":"https://static-cdn.jtvnw.net/badges/v1/bad/2","image_url_4x":"https://static-cdn.jtvnw.net/badges/v1/bad/3","title":"Bad"}""";
        string json = $$"""{"data":[{"set_id":"moderator","versions":[{{string.Join(",", Enumerable.Repeat(invalid, 100))}},{{Version(0)}}]}]}""";
        var handler = Handler(_ => Task.FromResult(EmoteTestData.Json(json)));

        IReadOnlyList<BadgeCatalogEntry> entries =
            await CreateSource(handler).GetGlobalAsync(CancellationToken.None);

        BadgeCatalogEntry entry = Assert.Single(entries);
        Assert.Equal("v0", entry.Version);
    }

    [Fact]
    public async Task Caps_titles_and_drops_ids_that_are_too_long()
    {
        string longSet = new('a', BadgeCatalogRules.MaxIdLength + 1);
        string longVersion = new('b', BadgeCatalogRules.MaxIdLength + 1);
        string longTitle = new('t', BadgeCatalogRules.MaxTitleLength + 20);
        string json = $$"""
            {"data":[
              {"set_id":"{{longSet}}","versions":[{{Version(0)}}]},
              {"set_id":"moderator","versions":[
                {"id":"{{longVersion}}","image_url_1x":"https://static-cdn.jtvnw.net/badges/v1/long/1","image_url_2x":"https://static-cdn.jtvnw.net/badges/v1/long/2","image_url_4x":"https://static-cdn.jtvnw.net/badges/v1/long/3","title":"Long"}
              ]},
              {"set_id":"vip","versions":[
                {"id":"1","image_url_1x":"https://static-cdn.jtvnw.net/badges/v1/title/1","image_url_2x":"https://static-cdn.jtvnw.net/badges/v1/title/2","image_url_4x":"https://static-cdn.jtvnw.net/badges/v1/title/3","title":"{{longTitle}}"}
              ]}
            ]}
            """;
        var handler = Handler(_ => Task.FromResult(EmoteTestData.Json(json)));

        IReadOnlyList<BadgeCatalogEntry> entries =
            await CreateSource(handler).GetGlobalAsync(CancellationToken.None);

        BadgeCatalogEntry entry = Assert.Single(entries);
        Assert.Equal("vip/1", $"{entry.Set}/{entry.Version}");
        Assert.Equal(BadgeCatalogRules.MaxTitleLength, entry.Title.Length);
    }

    [Fact]
    public async Task Caps_the_catalog_at_five_hundred_sets()
    {
        string json = $$"""
            {"data":[{{string.Join(",", Enumerable.Range(0, 501).Select(i => Set($"s{i}", 1)))}}]}
            """;
        var handler = Handler(_ => Task.FromResult(EmoteTestData.Json(json)));

        IReadOnlyList<BadgeCatalogEntry> entries =
            await CreateSource(handler).GetGlobalAsync(CancellationToken.None);

        Assert.Equal(BadgeCatalogRules.MaxSets, entries.Count);
        Assert.DoesNotContain(entries, entry => entry.Set == "s500");
    }

    [Fact]
    public async Task Caps_each_set_at_one_hundred_versions()
    {
        string json = $$"""{"data":[{{Set("moderator", 101)}}]}""";
        var handler = Handler(_ => Task.FromResult(EmoteTestData.Json(json)));

        IReadOnlyList<BadgeCatalogEntry> entries =
            await CreateSource(handler).GetGlobalAsync(CancellationToken.None);

        Assert.Equal(BadgeCatalogRules.MaxVersionsPerSet, entries.Count);
        Assert.DoesNotContain(entries, entry => entry.Version == "v100");
    }

    [Fact]
    public async Task An_oversized_body_is_a_too_large_failure()
    {
        var handler = Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[BadgeCatalogRules.MaxResponseBytes + 1]),
        }));

        BadgeCatalogException error = await Assert.ThrowsAsync<BadgeCatalogException>(
            () => CreateSource(handler).GetGlobalAsync(CancellationToken.None));

        Assert.Equal(BadgeCatalogFailureKind.TooLarge, error.Kind);
        Assert.Null(error.HttpStatus);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"data":null}""")]
    public async Task An_invalid_payload_is_reported_without_an_http_status(string json)
    {
        var handler = Handler(_ => Task.FromResult(EmoteTestData.Json(json)));

        BadgeCatalogException error = await Assert.ThrowsAsync<BadgeCatalogException>(
            () => CreateSource(handler).GetGlobalAsync(CancellationToken.None));

        Assert.Equal(BadgeCatalogFailureKind.InvalidPayload, error.Kind);
        Assert.Null(error.HttpStatus);
    }

    [Fact]
    public async Task A_non_success_response_carries_its_status()
    {
        var handler = Handler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        BadgeCatalogException error = await Assert.ThrowsAsync<BadgeCatalogException>(
            () => CreateSource(handler).GetGlobalAsync(CancellationToken.None));

        Assert.Equal(BadgeCatalogFailureKind.HttpStatus, error.Kind);
        Assert.Equal(503, error.HttpStatus);
    }

    [Fact]
    public async Task A_401_invalidates_the_token_and_retries_once()
    {
        int badgeCalls = 0;
        int tokens = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
                return Task.FromResult(BadgeTestData.Token(++tokens == 1 ? "one" : "two"));

            badgeCalls++;
            return Task.FromResult(badgeCalls == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : EmoteTestData.Json(CatalogJson));
        });

        IReadOnlyList<BadgeCatalogEntry> entries =
            await CreateSource(handler).GetGlobalAsync(CancellationToken.None);

        Assert.NotEmpty(entries);
        Assert.Equal(2, badgeCalls);
        Assert.Equal(2, tokens);
        string[] authorizations = handler.Requests
            .Where(request => request.RequestUri?.Host == "api.twitch.tv")
            .SelectMany(request => request.Headers)
            .Where(header => header.Key == "Authorization")
            .Select(header => header.Value)
            .ToArray();
        Assert.Equal(["Bearer one", "Bearer two"], authorizations);
    }

    private static TwitchBadgeSource CreateSource(HttpMessageHandler handler)
    {
        var options = Options.Create(new TwitchOptions
        {
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret",
        });
        var factory = new StubHttpClientFactory(handler);
        var clock = new FakeTimeProvider(Now);
        var tokens = new TwitchTokenProvider(
            factory, options, clock, NullLogger<TwitchTokenProvider>.Instance);
        return new TwitchBadgeSource(factory, tokens, options);
    }

    /// <summary>Answers token acquisition, then delegates badge requests to the test.</summary>
    private static StubHttpMessageHandler Handler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> badge) =>
        new((request, _) => request.RequestUri!.Host == "id.twitch.tv"
            ? Task.FromResult(BadgeTestData.Token())
            : badge(request));

    private static string Set(string set, int versions) =>
        $$"""
        {"set_id":"{{set}}","versions":[{{string.Join(",", Enumerable.Range(0, versions).Select(Version))}}]}
        """;

    private static string Version(int index) =>
        $$"""
        {"id":"v{{index}}","image_url_1x":"https://static-cdn.jtvnw.net/badges/v1/{{index}}/1","image_url_2x":"https://static-cdn.jtvnw.net/badges/v1/{{index}}/2","image_url_4x":"https://static-cdn.jtvnw.net/badges/v1/{{index}}/3","title":"v{{index}}"}
        """;

}
