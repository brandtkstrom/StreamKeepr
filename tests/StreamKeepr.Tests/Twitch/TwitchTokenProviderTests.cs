using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Tests.Twitch;

public sealed class TwitchTokenProviderTests
{
    [Fact]
    public async Task Caches_the_token_until_the_refresh_time()
    {
        var handler = new StubHandler(_ => Task.FromResult(Token("one", 3600)));
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero));
        var provider = CreateProvider(handler, clock);

        Assert.Equal("one", await provider.GetAsync(CancellationToken.None));
        Assert.Equal("one", await provider.GetAsync(CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Refreshes_five_minutes_early()
    {
        var handler = new StubHandler(_ => Task.FromResult(Token("one", 3600)));
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero));
        var provider = CreateProvider(handler, clock);

        await provider.GetAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(3301));
        await provider.GetAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Refresh_floor_is_one_minute()
    {
        var handler = new StubHandler(_ => Task.FromResult(Token("short", 120)));
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero));
        var provider = CreateProvider(handler, clock);

        await provider.GetAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(59));
        await provider.GetAsync(CancellationToken.None);
        Assert.Single(handler.Requests);

        clock.Advance(TimeSpan.FromSeconds(1));
        await provider.GetAsync(CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Invalidate_matching_token_forces_a_refresh()
    {
        int calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return Task.FromResult(Token($"token-{calls}", 3600));
        });
        var provider = CreateProvider(handler, TimeProvider.System);

        Assert.Equal("token-1", await provider.GetAsync(CancellationToken.None));
        provider.Invalidate("token-1");
        Assert.Equal("token-2", await provider.GetAsync(CancellationToken.None));
        provider.Invalidate("not-current");
        Assert.Equal("token-2", await provider.GetAsync(CancellationToken.None));

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Failed_token_request_throws_without_caching()
    {
        var handler = new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        var provider = CreateProvider(handler, TimeProvider.System);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetAsync(CancellationToken.None));
    }

    private static TwitchTokenProvider CreateProvider(
        HttpMessageHandler handler, TimeProvider clock) =>
        new(
            new StubHttpClientFactory(handler),
            Options.Create(new TwitchOptions
            {
                ClientId = "client-id",
                ClientSecret = "client-secret",
            }),
            clock);

    private static HttpResponseMessage Token(string value, int expiresIn) =>
        Json($"{{\"access_token\":\"{value}\",\"expires_in\":{expiresIn}}}");

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
}

internal sealed class StubHandler(
    Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return handler(request);
    }
}
