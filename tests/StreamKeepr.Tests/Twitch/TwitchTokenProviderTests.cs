using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Hosting;

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

    [Fact]
    public async Task Acquisition_logs_the_scheduled_refresh_without_the_token()
    {
        var handler = new StubHandler(_ => Task.FromResult(Token("secret-token-1", 3600)));
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero));
        var logs = new ListLoggerProvider();
        using ILoggerFactory factory = TestLogging.Factory(logs);
        var provider = CreateProvider(handler, clock, factory);

        await provider.GetAsync(CancellationToken.None);
        await provider.GetAsync(CancellationToken.None);

        CapturedLogEntry acquired = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, acquired.Level);
        Assert.Equal("initial", acquired.Properties["Reason"]);
        Assert.Equal(3600, acquired.Properties["ExpiresInSeconds"]);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 10, 17, 25, 0, TimeSpan.Zero),
            acquired.Properties["RefreshAt"]);
        AssertNoTokenValues(logs, "secret-token-1");
    }

    [Fact]
    public async Task A_due_refresh_logs_its_reason()
    {
        int calls = 0;
        var handler = new StubHandler(_ =>
            Task.FromResult(Token($"secret-token-{++calls}", 3600)));
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero));
        var logs = new ListLoggerProvider();
        using ILoggerFactory factory = TestLogging.Factory(logs);
        var provider = CreateProvider(handler, clock, factory);

        await provider.GetAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(3300));
        await provider.GetAsync(CancellationToken.None);

        Assert.Equal(
            ["initial", "refresh due"],
            logs.Entries.Select(entry => entry.Properties["Reason"]));
        AssertNoTokenValues(logs, "secret-token-1", "secret-token-2");
    }

    [Fact]
    public async Task Invalidation_is_logged_and_the_next_acquisition_names_it()
    {
        int calls = 0;
        var handler = new StubHandler(_ =>
            Task.FromResult(Token($"secret-token-{++calls}", 3600)));
        var logs = new ListLoggerProvider();
        using ILoggerFactory factory = TestLogging.Factory(logs);
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero));
        var provider = CreateProvider(handler, clock, factory);

        await provider.GetAsync(CancellationToken.None);
        provider.Invalidate("secret-token-1");
        await provider.GetAsync(CancellationToken.None);
        provider.Invalidate("secret-token-1");

        Assert.Collection(
            logs.Entries,
            entry => Assert.Equal("initial", entry.Properties["Reason"]),
            entry =>
            {
                Assert.Equal(LogLevel.Information, entry.Level);
                Assert.Contains("invalidated", entry.Message, StringComparison.Ordinal);
            },
            entry =>
            {
                Assert.Equal(LogLevel.Information, entry.Level);
                Assert.Equal("invalidated", entry.Properties["Reason"]);
            },
            entry =>
            {
                Assert.Equal(LogLevel.Debug, entry.Level);
                Assert.Contains("already replaced", entry.Message, StringComparison.Ordinal);
            });
        AssertNoTokenValues(logs, "secret-token-1", "secret-token-2");
    }

    private static void AssertNoTokenValues(ListLoggerProvider logs, params string[] tokens)
    {
        foreach (CapturedLogEntry entry in logs.Entries)
        {
            foreach (string token in tokens)
            {
                Assert.DoesNotContain(token, entry.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(
                    entry.Properties.Values,
                    value => value?.ToString()?.Contains(token, StringComparison.Ordinal) == true);
            }
        }
    }

    private static TwitchTokenProvider CreateProvider(
        HttpMessageHandler handler, TimeProvider clock, ILoggerFactory? logs = null) =>
        new(
            new StubHttpClientFactory(handler),
            Options.Create(new TwitchOptions
            {
                ClientId = "client-id",
                ClientSecret = "client-secret",
            }),
            clock,
            (logs ?? NullLoggerFactory.Instance).CreateLogger<TwitchTokenProvider>());

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
