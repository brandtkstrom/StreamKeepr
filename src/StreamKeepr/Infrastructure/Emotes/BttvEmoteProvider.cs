using System.Text.Json;

namespace StreamKeepr.Infrastructure.Emotes;

/// <summary>
/// BetterTTV's unauthenticated cached catalogs. This tracer bullet fetches the global catalog;
/// channel catalogs follow in a later change.
/// </summary>
internal sealed class BttvEmoteProvider(IHttpClientFactory httpFactory) : IEmoteProvider
{
    public const string ProviderId = "bttv";
    public const string GlobalPath = "3/cached/emotes/global";

    public string Id => ProviderId;

    public async Task<EmoteProviderCatalog> GetCatalogAsync(
        EmoteChannel channel, CancellationToken ct)
    {
        using HttpClient http = httpFactory.CreateClient(EmoteHttpClients.Bttv);
        using HttpResponseMessage response = await http.GetAsync(
            GlobalPath, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new EmoteProviderException(
                ProviderId,
                (int)response.StatusCode,
                $"BetterTTV global catalog request failed with HTTP {(int)response.StatusCode}.");
        }

        byte[] body = await ReadBodyAsync(response, ct);
        BttvEmote[]? emotes;
        try
        {
            emotes = JsonSerializer.Deserialize<BttvEmote[]>(body, BttvJson.Options);
        }
        catch (JsonException)
        {
            throw new EmoteProviderException(
                ProviderId, (int)response.StatusCode,
                "BetterTTV returned an invalid global catalog.");
        }

        if (emotes is null)
        {
            throw new EmoteProviderException(
                ProviderId, (int)response.StatusCode,
                "BetterTTV returned an invalid global catalog.");
        }

        var entries = new List<EmoteCatalogEntry>(emotes.Length);
        foreach (BttvEmote? emote in emotes)
        {
            if (emote is null ||
                !EmoteCatalogRules.IsValidEmoteId(emote.Id) ||
                !EmoteCatalogRules.IsValidCode(emote.Code) ||
                emote.Modifier ||
                emote.Restrictions is { } restrictions &&
                restrictions.ValueKind != JsonValueKind.Null)
            {
                continue;
            }

            entries.Add(new EmoteCatalogEntry(
                emote.Id!, emote.Code!, emote.Animated, Modifier: false));
        }

        return new EmoteProviderCatalog([], entries);
    }

    private static async Task<byte[]> ReadBodyAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > EmoteCatalogRules.MaxResponseBytes)
            throw TooLarge();

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > EmoteCatalogRules.MaxResponseBytes)
                throw TooLarge();

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static EmoteProviderException TooLarge() =>
        new(ProviderId, 200, "BetterTTV catalog response exceeded two mebibytes.");

    public EmoteImageSet GetStaticImage(EmoteCatalogEntry entry)
    {
        string folder = entry.Animated ? "static/" : "";
        string Url(string size) =>
            $"https://cdn.betterttv.net/emote/{entry.Id}/{folder}{size}.webp";

        return new EmoteImageSet(
            Url("1x"), $"{Url("1x")} 1x, {Url("2x")} 2x, {Url("3x")} 3x");
    }
}

internal sealed record BttvEmote(
    string? Id,
    string? Code,
    bool Animated = false,
    bool Modifier = false,
    JsonElement? Restrictions = null);

internal static class BttvJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
