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
    public const string DisplayName = "BetterTTV";
    public const string GlobalPath = "3/cached/emotes/global";
    public const string ChannelPath = "3/cached/users/twitch";

    private const string ImageHost = "https://cdn.betterttv.net";

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
            throw EmoteProviderException.HttpFailure(ProviderId, DisplayName, response, "global");

        byte[] body = await EmoteResponseBody.ReadAsync(response, ProviderId, DisplayName, ct);
        BttvEmote[]? emotes;
        try
        {
            emotes = JsonSerializer.Deserialize<BttvEmote[]>(body, BttvJson.Options);
        }
        catch (JsonException)
        {
            throw EmoteProviderException.InvalidPayload(ProviderId, DisplayName, "global");
        }

        return emotes is null
            ? throw EmoteProviderException.InvalidPayload(ProviderId, DisplayName, "global")
            : Normalize(emotes);
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
            throw EmoteProviderException.HttpFailure(ProviderId, DisplayName, response, "channel");

        byte[] body = await EmoteResponseBody.ReadAsync(response, ProviderId, DisplayName, ct);
        BttvChannel? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BttvChannel>(body, BttvJson.Options);
        }
        catch (JsonException)
        {
            throw EmoteProviderException.InvalidPayload(ProviderId, DisplayName, "channel");
        }

        if (payload is null)
            throw EmoteProviderException.InvalidPayload(ProviderId, DisplayName, "channel");

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
                !EmoteCatalogRules.IsValidBttvEmoteId(emote.Id) ||
                !EmoteCatalogRules.IsValidCode(emote.Code) ||
                emote.Modifier ||
                HasRestrictions(emote.Restrictions))
            {
                continue;
            }

            entries.Add(new EmoteCatalogEntry(emote.Id!, emote.Code!, emote.Animated));
        }

        return [.. entries];
    }

    /// <summary>
    /// Absent, null, and empty restrictions mean "available to every viewer"; only a non-empty
    /// array or object restricts the emote, and restricted emotes are dropped because there is
    /// no viewer identity to evaluate them.
    /// </summary>
    private static bool HasRestrictions(JsonElement? restrictions)
    {
        if (restrictions is not { } value)
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => value.EnumerateObject().Any(),
            _ => true,
        };
    }

    public EmoteImageSet GetImageSet(EmoteCatalogEntry entry) =>
        BttvEmoteImage.Create(ImageHost, entry, options.Value.Emotes.Animated);
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
