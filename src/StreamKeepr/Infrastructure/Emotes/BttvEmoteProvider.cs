using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Infrastructure.Emotes;

/// <summary>
/// BetterTTV's unauthenticated cached catalogs: the global catalog for every channel and the
/// channel catalog when a numeric Twitch user ID is known.
/// </summary>
internal sealed class BttvEmoteProvider(
    IHttpClientFactory httpFactory,
    IOptions<ChatOptions> options) : IEmoteProvider
{
    public const string ProviderId = "bttv";
    public const string GlobalPath = "3/cached/emotes/global";
    public const string ChannelPath = "3/cached/users/twitch";

    public string Id => ProviderId;

    public bool Enabled => options.Value.Emotes.Bttv.Enabled;

    public async Task<IReadOnlyList<EmoteCatalogEntry>> GetGlobalAsync(CancellationToken ct)
    {
        using HttpClient http = httpFactory.CreateClient(EmoteHttpClients.Bttv);
        return await FetchGlobalAsync(http, ct);
    }

    public async Task<IReadOnlyList<EmoteCatalogEntry>> GetChannelAsync(
        string channelId, CancellationToken ct)
    {
        if (!EmoteCatalogRules.IsValidChannelId(channelId))
            throw new ArgumentException("A numeric Twitch user ID is required.", nameof(channelId));

        using HttpClient http = httpFactory.CreateClient(EmoteHttpClients.Bttv);
        return await FetchChannelAsync(http, channelId, ct);
    }

    private static async Task<IReadOnlyList<EmoteCatalogEntry>> FetchGlobalAsync(
        HttpClient http, CancellationToken ct)
    {
        using HttpResponseMessage response = await http.GetAsync(
            GlobalPath, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw Failure(response, "global");

        byte[] body = await ReadBodyAsync(response, ct);
        BttvEmote[]? emotes;
        try
        {
            emotes = JsonSerializer.Deserialize<BttvEmote[]>(body, BttvJson.Options);
        }
        catch (JsonException)
        {
            throw Invalid("global");
        }

        return emotes is null ? throw Invalid("global") : Normalize(emotes);
    }

    private static async Task<IReadOnlyList<EmoteCatalogEntry>> FetchChannelAsync(
        HttpClient http, string channelId, CancellationToken ct)
    {
        using HttpResponseMessage response = await http.GetAsync(
            $"{ChannelPath}/{channelId}", HttpCompletionOption.ResponseHeadersRead, ct);

        // A numeric ID with no BetterTTV profile is an available channel without emotes.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        if (!response.IsSuccessStatusCode)
            throw Failure(response, "channel");

        byte[] body = await ReadBodyAsync(response, ct);
        BttvChannel? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BttvChannel>(body, BttvJson.Options);
        }
        catch (JsonException)
        {
            throw Invalid("channel");
        }

        if (payload is null)
            throw Invalid("channel");

        // Shared entries precede channel entries so shared codes win the first-entry-wins dedupe.
        return [.. Normalize(payload.SharedEmotes), .. Normalize(payload.ChannelEmotes)];
    }

    private static EmoteCatalogEntry[] Normalize(IEnumerable<BttvEmote?>? emotes)
    {
        if (emotes is null)
            return [];

        var entries = new List<EmoteCatalogEntry>();
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

            entries.Add(new EmoteCatalogEntry(emote.Id!, emote.Code!, emote.Animated));
        }

        return [.. entries];
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
        new(
            ProviderId,
            httpStatus: null,
            "BetterTTV catalog response exceeded two mebibytes.",
            EmoteProviderFailureKind.TooLarge);

    private static EmoteProviderException Failure(HttpResponseMessage response, string scope) =>
        new(
            ProviderId,
            (int)response.StatusCode,
            $"BetterTTV {scope} catalog request failed with HTTP {(int)response.StatusCode}.");

    private static EmoteProviderException Invalid(string scope) =>
        new(
            ProviderId,
            httpStatus: null,
            $"BetterTTV returned an invalid {scope} catalog.",
            EmoteProviderFailureKind.InvalidPayload);

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

/// <summary>The BetterTTV channel payload; only the two emote lists are used.</summary>
internal sealed record BttvChannel(
    BttvEmote[]? ChannelEmotes = null,
    BttvEmote[]? SharedEmotes = null);

internal static class BttvJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
