using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// Caches Twitch's app access token and refreshes it five minutes early (never sooner than
/// one minute from now). A 401 invalidates the token so the caller can retry once.
/// </summary>
public sealed class TwitchTokenProvider(
    IHttpClientFactory httpFactory,
    IOptions<TwitchOptions> options,
    TimeProvider clock) : IDisposable
{
    private sealed record CachedToken(string Value, DateTimeOffset RefreshAt);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile CachedToken? _cached;

    public async Task<string> GetAsync(CancellationToken ct)
    {
        if (_cached is { } hit && clock.GetUtcNow() < hit.RefreshAt)
            return hit.Value;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is { } again && clock.GetUtcNow() < again.RefreshAt)
                return again.Value;

            TwitchOptions twitch = options.Value;
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = twitch.ClientId,
                ["client_secret"] = twitch.ClientSecret,
                ["grant_type"] = "client_credentials",
            });

            using HttpClient http = httpFactory.CreateClient(TwitchHttpClients.Auth);
            using HttpResponseMessage response = await http.PostAsync("oauth2/token", form, ct);
            response.EnsureSuccessStatusCode();

            TwitchTokenResponse? body = await response.Content.ReadFromJsonAsync(
                TwitchJsonContext.Default.TwitchTokenResponse, ct);

            if (body is null || string.IsNullOrWhiteSpace(body.AccessToken) || body.ExpiresIn <= 0)
                throw new InvalidDataException("Twitch returned an invalid app token response.");

            // Refresh five minutes early, with a one-minute floor.
            var refreshAt = clock.GetUtcNow().AddSeconds(Math.Max(60, body.ExpiresIn - 300));
            _cached = new CachedToken(body.AccessToken, refreshAt);
            return body.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the cached token only if it is still the rejected value.</summary>
    public void Invalidate(string staleToken)
    {
        if (_cached?.Value == staleToken)
            _cached = null;
    }

    public void Dispose() => _gate.Dispose();
}
