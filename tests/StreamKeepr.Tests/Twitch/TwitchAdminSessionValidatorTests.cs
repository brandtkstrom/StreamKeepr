using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Tests.Twitch;

public sealed class TwitchAdminSessionValidatorTests
{
    private static readonly ClaimsPrincipal Admin =
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "mock_admin")], "test"));

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    public async Task Only_a_401_from_validate_expires_the_session(HttpStatusCode status, bool valid)
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(status));
        var validator = Create(handler, token: "access-1");

        Assert.Equal(valid, await validator.IsValidAsync(Admin, CancellationToken.None));

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("https://id.twitch.tv/oauth2/validate", request.RequestUri?.ToString());
    }

    [Fact]
    public async Task A_missing_token_expires_the_session_without_calling_twitch()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.False(await Create(handler, token: null).IsValidAsync(Admin, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_unreachable_twitch_keeps_the_session()
    {
        var handler = new RecordingHttpMessageHandler(_ => throw new HttpRequestException("down"));

        Assert.True(await Create(handler, token: "access-1").IsValidAsync(Admin, CancellationToken.None));
    }

    private static TwitchAdminSessionValidator Create(HttpMessageHandler handler, string? token) =>
        new(new FixedTokenStore(token), new StubHttpClientFactory(handler),
            NullLogger<TwitchAdminSessionValidator>.Instance);

    private sealed class FixedTokenStore(string? token) : IAdminTokenStore
    {
        public event Action? Changed { add { } remove { } }

        public ValueTask<string?> GetAsync(CancellationToken ct) => ValueTask.FromResult(token);

        public ValueTask<string?> RefreshAsync(CancellationToken ct) => ValueTask.FromResult(token);

        public Task SaveAsync(AdminToken token, CancellationToken ct) => Task.CompletedTask;

        public Task ClearAsync(CancellationToken ct) => Task.CompletedTask;

        public Task WaitForTokenAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
