using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Infrastructure.Emotes;

/// <summary>
/// FrankerFaceZ's unauthenticated v1 catalogs. The global catalog reads only the sets listed in
/// <c>default_sets</c>; the room catalog is keyed by numeric Twitch user ID. <c>public</c> means
/// addable elsewhere, not renderable, so it is ignored; <c>hidden</c> entries are skipped because
/// FFZ masks their codes as <c>???</c> in chat. An emote's <c>animated</c> property is a
/// density-to-URL map, not a boolean, and is absent on static emotes.
/// </summary>
internal sealed class FfzEmoteProvider(
    IHttpClientFactory httpFactory,
    IOptions<ChatOptions> options) : IEmoteProvider
{
    public const string ProviderId = "ffz";
    public const string DisplayName = "FrankerFaceZ";
    public const string GlobalPath = "v1/set/global";
    public const string RoomPath = "v1/room/id";

    private const string ImageHost = "https://cdn.frankerfacez.com";

    /// <summary>FFZ publishes 1x, 2x, and 4x images; there is no 3x asset.</summary>
    private static readonly string[] KnownScales = ["1", "2", "4"];

    public string Id => ProviderId;

    public bool Enabled => options.Value.Emotes.Ffz.Enabled;

    public async Task<IReadOnlyList<EmoteCatalogEntry>> GetGlobalAsync(CancellationToken ct)
    {
        using HttpClient http = httpFactory.CreateClient(EmoteHttpClients.Ffz);
        using HttpResponseMessage response = await http.GetAsync(
            GlobalPath, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
            throw EmoteProviderException.HttpFailure(ProviderId, DisplayName, response, "global");

        FfzGlobalCatalog? catalog = await ReadCatalogAsync<FfzGlobalCatalog>(
            response, "global", ct);

        return catalog is null
            ? throw EmoteProviderException.InvalidPayload(ProviderId, DisplayName, "global")
            : NormalizeGlobal(catalog);
    }

    public async Task<IReadOnlyList<EmoteCatalogEntry>> GetChannelAsync(
        string channelId, CancellationToken ct)
    {
        if (!EmoteCatalogRules.IsValidChannelId(channelId))
            throw new ArgumentException("A numeric Twitch user ID is required.", nameof(channelId));

        using HttpClient http = httpFactory.CreateClient(EmoteHttpClients.Ffz);
        using HttpResponseMessage response = await http.GetAsync(
            $"{RoomPath}/{channelId}", HttpCompletionOption.ResponseHeadersRead, ct);

        // A numeric ID with no FFZ room is an available channel without FFZ emotes.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        if (!response.IsSuccessStatusCode)
            throw EmoteProviderException.HttpFailure(ProviderId, DisplayName, response, "channel");

        FfzRoomCatalog? catalog = await ReadCatalogAsync<FfzRoomCatalog>(
            response, "channel", ct);

        return catalog is null
            ? throw EmoteProviderException.InvalidPayload(ProviderId, DisplayName, "channel")
            : NormalizeRoom(catalog);
    }

    private static async Task<T?> ReadCatalogAsync<T>(
        HttpResponseMessage response, string scope, CancellationToken ct)
        where T : class
    {
        byte[] body = await EmoteResponseBody.ReadAsync(response, ProviderId, DisplayName, ct);
        try
        {
            return JsonSerializer.Deserialize<T>(body, FfzJson.Options);
        }
        catch (JsonException)
        {
            throw EmoteProviderException.InvalidPayload(ProviderId, DisplayName, scope);
        }
    }

    private static EmoteCatalogEntry[] NormalizeGlobal(FfzGlobalCatalog catalog)
    {
        if (catalog.DefaultSets is not { Length: > 0 } || catalog.Sets is null)
            return [];

        var sets = new List<FfzSet>();
        foreach (long setId in catalog.DefaultSets)
        {
            // A default set can be absent from the sets map or null; that set is skipped.
            if (catalog.Sets.TryGetValue(setId, out FfzSet? set) && set is not null)
                sets.Add(set);
        }

        return NormalizeSets(sets);
    }

    private static EmoteCatalogEntry[] NormalizeRoom(FfzRoomCatalog catalog)
    {
        if (catalog.Sets is null)
            return [];

        // Set order is not part of the payload contract; order by ID so exact-code duplicates
        // resolve deterministically.
        return NormalizeSets(catalog.Sets
            .OrderBy(entry => entry.Key)
            .Select(entry => entry.Value));
    }

    private static EmoteCatalogEntry[] NormalizeSets(IEnumerable<FfzSet?>? sets)
    {
        if (sets is null)
            return [];

        var entries = new List<EmoteCatalogEntry>();
        foreach (FfzSet? set in sets)
        {
            if (set is not null)
                entries.AddRange(Normalize(set.Emoticons));
        }

        return [.. entries];
    }

    private static EmoteCatalogEntry[] Normalize(IEnumerable<FfzEmote?>? emotes)
    {
        if (emotes is null)
            return [];

        var entries = new List<EmoteCatalogEntry>();
        foreach (FfzEmote? emote in emotes)
        {
            if (emote is null ||
                emote.Modifier ||
                emote.Hidden ||
                !TryGetEmoteId(emote.Id, out string? id) ||
                !EmoteCatalogRules.IsValidCode(emote.Name) ||
                !TryGetScales(emote.Urls, out string[] scales))
            {
                continue;
            }

            bool animated = IsAnimated(emote.Animated);
            entries.Add(new EmoteCatalogEntry(
                id!,
                emote.Name!,
                animated,
                scales,
                animated ? AvailableScales(emote.Animated) : null));
        }

        return [.. entries];
    }

    private static bool TryGetEmoteId(JsonElement? element, out string? id)
    {
        id = null;
        if (element is not JsonElement value ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long numeric) ||
            numeric <= 0)
        {
            return false;
        }

        id = numeric.ToString(CultureInfo.InvariantCulture);
        return EmoteCatalogRules.IsValidFfzEmoteId(id);
    }

    private static bool TryGetScales(Dictionary<string, string>? urls, out string[] scales)
    {
        // The 1x asset is mandatory; a payload that omits it cannot render the emote.
        scales = AvailableScales(urls);
        return scales.Length > 0 && scales[0] == "1";
    }

    private static string[] AvailableScales(Dictionary<string, string>? map)
    {
        if (map is null)
            return [];

        return [.. KnownScales.Where(scale =>
            map.TryGetValue(scale, out string? url) && !string.IsNullOrEmpty(url))];
    }

    private static bool IsAnimated(Dictionary<string, string>? animated) =>
        animated is not null &&
        animated.TryGetValue("1", out string? url) &&
        !string.IsNullOrEmpty(url);

    public EmoteImageSet GetImageSet(EmoteCatalogEntry entry)
    {
        // FFZ publishes 1x/2x/4x; the active static or animated map supplies the advertised
        // keys, so an emote without a size cannot produce a candidate that fails to load.
        bool serveAnimated = entry.Animated && options.Value.Emotes.Animated;
        IReadOnlyList<string>? advertised = serveAnimated
            ? entry.AnimatedImageScales
            : entry.ImageScales;
        string[] scales = advertised is { Count: > 0 } available
            ? [.. KnownScales.Where(available.Contains)]
            : KnownScales;

        string folder = serveAnimated ? "animated/" : "";

        string Url(string scale) => $"{ImageHost}/emote/{entry.Id}/{folder}{scale}";

        return new EmoteImageSet(
            Url(scales[0]),
            string.Join(", ", scales.Select(scale => $"{Url(scale)} {scale}x")));
    }
}

/// <summary>The FFZ global payload; only <c>default_sets</c> and the set map are read.</summary>
internal sealed record FfzGlobalCatalog(
    [property: JsonPropertyName("default_sets")] long[]? DefaultSets = null,
    [property: JsonPropertyName("sets")] Dictionary<long, FfzSet>? Sets = null);

/// <summary>The FFZ room payload; the room metadata is not needed for normalization.</summary>
internal sealed record FfzRoomCatalog(
    [property: JsonPropertyName("sets")] Dictionary<long, FfzSet>? Sets = null);

internal sealed record FfzSet(
    [property: JsonPropertyName("emoticons")] FfzEmote[]? Emoticons = null);

/// <summary>
/// One FFZ emote. <c>Id</c> stays a <see cref="JsonElement"/> so an entry with a non-numeric or
/// out-of-range ID is dropped instead of failing the whole catalog. <c>Animated</c> is the API's
/// density-to-URL map; its <c>1</c> key is the intrinsic animation flag.
/// </summary>
internal sealed record FfzEmote(
    [property: JsonPropertyName("id")] JsonElement? Id = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("modifier")] bool Modifier = false,
    [property: JsonPropertyName("hidden")] bool Hidden = false,
    [property: JsonPropertyName("urls")] Dictionary<string, string>? Urls = null,
    [property: JsonPropertyName("animated")] Dictionary<string, string>? Animated = null);

internal static class FfzJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
