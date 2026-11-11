using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using StreamKeepr.Ui;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// Validates the stored admin token with <c>GET id.twitch.tv/oauth2/validate</c>. Only a
/// missing token or a 401 means the session expired; network and server failures keep the
/// admin signed in so a Twitch outage never signs anyone out.
/// </summary>
public sealed class TwitchAdminSessionValidator(
    IAdminTokenStore tokens,
    IHttpClientFactory httpFactory,
    ILogger<TwitchAdminSessionValidator> logger) : IAdminSessionValidator
{
    public async Task<bool> IsValidAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        // GetAsync refreshes near expiry and clears a token Twitch no longer accepts.
        string? accessToken = await tokens.GetAsync(ct);
        if (accessToken is null)
            return false;

        try
        {
            using HttpClient http = httpFactory.CreateClient(TwitchHttpClients.Auth);
            using var request = new HttpRequestMessage(HttpMethod.Get, "oauth2/validate");
            request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
            using HttpResponseMessage response = await http.SendAsync(request, ct);
            return response.StatusCode != HttpStatusCode.Unauthorized;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not reach Twitch to validate the admin session; keeping it.");
            return true;
        }
    }
}

/// <summary>The Mock profile's fake admin session never expires on its own.</summary>
public sealed class AlwaysValidAdminSessionValidator : IAdminSessionValidator
{
    public Task<bool> IsValidAsync(ClaimsPrincipal user, CancellationToken ct) => Task.FromResult(true);
}
