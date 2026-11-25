using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Http;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Infrastructure.Badges;

/// <summary>
/// Fetches Twitch's global chat badge catalog through Helix with an app access token. The token
/// and the response body stay server-side; the browser only ever receives normalized image URLs.
/// A 401 invalidates the token once and retries, matching the other Helix readers.
/// </summary>
internal sealed class TwitchBadgeSource(
    IHttpClientFactory httpFactory,
    TwitchTokenProvider tokens,
    IOptions<TwitchOptions> options) : IBadgeSource
{
    public const string GlobalPath = "helix/chat/badges/global";

    public async Task<IReadOnlyList<BadgeCatalogEntry>> GetGlobalAsync(CancellationToken ct)
    {
        using HttpClient http = httpFactory.CreateClient(TwitchHttpClients.Helix);

        for (int attempt = 0; ; attempt++)
        {
            string token = await tokens.GetAsync(ct);

            using var request = new HttpRequestMessage(HttpMethod.Get, GlobalPath);
            request.Headers.Add("Client-Id", options.Value.ClientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                tokens.Invalidate(token);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw BadgeCatalogException.HttpFailure((int)response.StatusCode);

            byte[] body = await ResponseBodyReader.ReadAsync(
                response,
                BadgeCatalogRules.MaxResponseBytes,
                BadgeCatalogException.ResponseTooLarge,
                ct);
            return Normalize(body);
        }
    }

    /// <summary>
    /// Validates and flattens the Helix payload. Malformed set/version IDs and versions without a
    /// complete, trusted image set are dropped; the first 500 sets and 100 versions of each are
    /// kept. A body that is not the documented envelope is an invalid payload.
    /// </summary>
    private static IReadOnlyList<BadgeCatalogEntry> Normalize(byte[] body)
    {
        HelixGlobalBadgesResponse? payload;
        try
        {
            payload = JsonSerializer.Deserialize(
                body, TwitchJsonContext.Default.HelixGlobalBadgesResponse);
        }
        catch (JsonException)
        {
            throw BadgeCatalogException.InvalidPayload();
        }

        if (payload?.Data is null)
            throw BadgeCatalogException.InvalidPayload();

        var entries = new List<BadgeCatalogEntry>();
        int setCount = 0;
        foreach (HelixBadgeSet? set in payload.Data)
        {
            if (setCount == BadgeCatalogRules.MaxSets)
                break;

            if (set is null ||
                !BadgeCatalogRules.IsValidId(set.SetId) ||
                set.Versions is null)
            {
                continue;
            }

            string setKey = set.SetId!;
            int versionCount = 0;
            int setEntries = entries.Count;
            foreach (HelixBadgeVersion? version in set.Versions)
            {
                if (versionCount == BadgeCatalogRules.MaxVersionsPerSet)
                    break;

                if (version is null || !BadgeCatalogRules.IsValidId(version.Id))
                    continue;

                string? srcSet = BadgeCatalogRules.BuildSrcSet(
                    version.ImageUrl1X, version.ImageUrl2X, version.ImageUrl4X);
                if (srcSet is null)
                    continue;

                entries.Add(new BadgeCatalogEntry(
                    setKey,
                    version.Id!,
                    BadgeCatalogRules.NormalizeTitle(version.Title, setKey),
                    version.ImageUrl1X!,
                    srcSet));
                versionCount++;
            }

            // A set with no usable versions does not consume the set cap.
            if (entries.Count > setEntries)
                setCount++;
        }

        return entries;
    }
}
