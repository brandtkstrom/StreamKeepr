using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Infrastructure.Emotes;

/// <summary>
/// 7TV global and channel emotes through the shared provider seam. The set-entry <c>name</c> is
/// the code viewers type; the nested emote <c>name</c> is the emote's original name and differs
/// for renamed emotes, so only the set entry is read. An animated emote's still frame is the
/// <c>_static</c> file, served unless <c>Chat:Emotes:Animated</c> asks for the animation itself.
/// </summary>
internal sealed class SevenTvEmoteProvider(
    IHttpClientFactory httpFactory,
    IOptions<ChatOptions> options) : IEmoteProvider
{
    public const string ProviderId = "7tv";
    public const string DisplayName = "7TV";
    public const string GlobalPath = "v3/emote-sets/global";
    public const string UserPath = "v3/users/twitch";
    public const string EmoteSetPath = "v3/emote-sets";

    private const string ImageHost = "https://cdn.7tv.app";

    /// <summary>Set-entry flag bit 0 marks a zero-width emote, which v1 does not stack.</summary>
    private const int ZeroWidthFlag = 1;

    public string Id => ProviderId;

    public bool Enabled => options.Value.Emotes.SevenTv.Enabled;

    public async Task<IReadOnlyList<EmoteCatalogEntry>> GetGlobalAsync(CancellationToken ct)
    {
        using HttpClient http = httpFactory.CreateClient(EmoteHttpClients.SevenTv);
        return await FetchEmoteSetAsync(http, GlobalPath, "global", notFoundIsEmpty: false, ct);
    }

    public async Task<IReadOnlyList<EmoteCatalogEntry>> GetChannelAsync(
        string channelId, CancellationToken ct)
    {
        if (!EmoteCatalogRules.IsValidChannelId(channelId))
            throw new ArgumentException("A numeric Twitch user ID is required.", nameof(channelId));

        using HttpClient http = httpFactory.CreateClient(EmoteHttpClients.SevenTv);
        using HttpResponseMessage response = await http.GetAsync(
            $"{UserPath}/{channelId}", HttpCompletionOption.ResponseHeadersRead, ct);

        // A numeric ID with no 7TV profile is an available channel without emotes.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        if (!response.IsSuccessStatusCode)
            throw EmoteProviderException.HttpFailure(ProviderId, DisplayName, response, "channel");

        byte[] body = await EmoteResponseBody.ReadAsync(response, ProviderId, DisplayName, ct);
        SevenTvUser? user;
        try
        {
            user = JsonSerializer.Deserialize<SevenTvUser>(body, SevenTvJson.Options);
        }
        catch (JsonException)
        {
            throw EmoteProviderException.InvalidPayload(ProviderId, DisplayName, "channel");
        }

        if (user is null)
            throw EmoteProviderException.InvalidPayload(ProviderId, DisplayName, "channel");

        if (user.EmoteSet is not null)
            return Normalize(user.EmoteSet.Emotes);

        // The documented shape can carry only the set ID; the ID comes from an untrusted payload,
        // so it is validated before it becomes a path segment.
        if (user.EmoteSetId is not { } setId)
            return [];

        if (!EmoteCatalogRules.IsValidSevenTvId(setId))
            throw EmoteProviderException.InvalidPayload(ProviderId, DisplayName, "channel");

        return await FetchEmoteSetAsync(http, $"{EmoteSetPath}/{setId}", "channel", notFoundIsEmpty: true, ct);
    }

    private static async Task<IReadOnlyList<EmoteCatalogEntry>> FetchEmoteSetAsync(
        HttpClient http, string path, string scope, bool notFoundIsEmpty, CancellationToken ct)
    {
        using HttpResponseMessage response = await http.GetAsync(
            path, HttpCompletionOption.ResponseHeadersRead, ct);

        // A channel whose set was removed is an available channel without emotes. The global set
        // is never absent, so a 404 there stays a failure.
        if (response.StatusCode == HttpStatusCode.NotFound && notFoundIsEmpty)
            return [];

        if (!response.IsSuccessStatusCode)
            throw EmoteProviderException.HttpFailure(ProviderId, DisplayName, response, scope);

        byte[] body = await EmoteResponseBody.ReadAsync(response, ProviderId, DisplayName, ct);
        SevenTvEmoteSet? set;
        try
        {
            set = JsonSerializer.Deserialize<SevenTvEmoteSet>(body, SevenTvJson.Options);
        }
        catch (JsonException)
        {
            throw EmoteProviderException.InvalidPayload(ProviderId, DisplayName, scope);
        }

        return set is null
            ? throw EmoteProviderException.InvalidPayload(ProviderId, DisplayName, scope)
            : Normalize(set.Emotes);
    }

    private static EmoteCatalogEntry[] Normalize(IEnumerable<SevenTvSetEmote?>? emotes)
    {
        if (emotes is null)
            return [];

        var entries = new List<EmoteCatalogEntry>();
        foreach (SevenTvSetEmote? emote in emotes)
        {
            if (emote is null ||
                !EmoteCatalogRules.IsValidSevenTvId(emote.Id) ||
                !EmoteCatalogRules.IsValidCode(emote.Name) ||
                (emote.Flags & ZeroWidthFlag) != 0)
            {
                continue;
            }

            entries.Add(new EmoteCatalogEntry(emote.Id!, emote.Name!, emote.Data?.Animated ?? false));
        }

        return [.. entries];
    }

    public EmoteImageSet GetImageSet(EmoteCatalogEntry entry)
    {
        // 7TV suffixes the still frame of an animated emote; its animated asset has no suffix, and
        // a non-animated emote has no _static file at all.
        string suffix = entry.Animated && !options.Value.Emotes.Animated ? "_static" : "";

        return EmoteImageSet.ForSizes(size => $"{ImageHost}/emote/{entry.Id}/{size}{suffix}.webp");
    }
}

/// <summary>The 7TV user payload; the catalog arrives inline or by set ID.</summary>
internal sealed record SevenTvUser(
    [property: JsonPropertyName("emote_set_id")] string? EmoteSetId = null,
    [property: JsonPropertyName("emote_set")] SevenTvEmoteSet? EmoteSet = null);

internal sealed record SevenTvEmoteSet(
    [property: JsonPropertyName("emotes")] SevenTvSetEmote[]? Emotes = null);

/// <summary>
/// One emote as the set lists it. <c>Name</c> is the channel's chosen code; <c>Flags</c> is the
/// set-entry flag word (bit 0 is zero-width), not the emote's own flag word.
/// </summary>
internal sealed record SevenTvSetEmote(
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("flags")] int Flags = 0,
    [property: JsonPropertyName("data")] SevenTvEmoteData? Data = null);

internal sealed record SevenTvEmoteData(
    [property: JsonPropertyName("animated")] bool Animated = false);

internal static class SevenTvJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
