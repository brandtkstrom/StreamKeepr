namespace StreamKeepr.Infrastructure.Emotes;

/// <summary>Validation and size limits shared by provider normalization and aggregation.</summary>
internal static class EmoteCatalogRules
{
    public const int MaxEntriesPerCatalog = 2_000;
    public const int MaxCodeLength = 128;

    /// <summary>
    /// Provider payloads carry a deep owner/connection graph per emote, so a full 7TV channel
    /// catalog is several times larger than a BetterTTV one; the cap bounds every provider.
    /// </summary>
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>BetterTTV emote IDs are 24 hexadecimal characters.</summary>
    public static bool IsValidBttvEmoteId(string? id)
    {
        if (id is not { Length: 24 })
            return false;

        foreach (char c in id)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 7TV emote and emote-set IDs are 26-character ULIDs. Crockford's alphabet is a subset of
    /// this range, so the check cannot reject a real ID; it pins the length and alphabet that
    /// keep the ID a single path segment of a fixed URL template.
    /// </summary>
    public static bool IsValidSevenTvEmoteId(string? id) =>
        id is { Length: 26 } && id.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c));

    public static bool IsValidCode(string? code) =>
        !string.IsNullOrEmpty(code) && code.Length <= MaxCodeLength;

    /// <summary>Twitch user IDs are numeric and at most 20 digits.</summary>
    public static bool IsValidChannelId(string? channelId) =>
        channelId is { Length: >= 1 and <= 20 } && channelId.All(char.IsAsciiDigit);

    /// <summary>Invalid identifiers never leave a provider; the service enforces it again.</summary>
    public static bool IsUsable(EmoteCatalogEntry entry) =>
        (IsValidBttvEmoteId(entry.Id) || IsValidSevenTvEmoteId(entry.Id)) &&
        IsValidCode(entry.Code);
}
