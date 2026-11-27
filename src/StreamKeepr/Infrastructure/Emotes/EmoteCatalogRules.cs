namespace StreamKeepr.Infrastructure.Emotes;

/// <summary>Validation and size limits shared by provider normalization and aggregation.</summary>
internal static class EmoteCatalogRules
{
    public const int MaxEntriesPerCatalog = 2_000;
    public const int MaxCodeLength = 128;

    /// <summary>
    /// Provider payloads carry a deep owner/connection graph per emote: the largest sampled 7TV
    /// channel catalog was 2.38 MB, while a sampled BetterTTV channel catalog was 64 KB. The cap
    /// bounds every provider.
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
    public static bool IsValidSevenTvId(string? id) =>
        id is { Length: 26 } && id.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c));

    /// <summary>
    /// FrankerFaceZ emote IDs are positive decimal integers. The bound keeps the ID a single
    /// path segment of a fixed URL template; current IDs are small integers, not fixed-width.
    /// </summary>
    public static bool IsValidFfzEmoteId(string? id) =>
        IsBoundedNumeric(id, maxLength: 20) && id!.Any(c => c != '0');

    public static bool IsValidCode(string? code) =>
        !string.IsNullOrEmpty(code) && code.Length <= MaxCodeLength;

    /// <summary>Twitch user IDs are numeric and at most 20 digits.</summary>
    public static bool IsValidChannelId(string? channelId) =>
        IsBoundedNumeric(channelId, maxLength: 20);

    private static bool IsBoundedNumeric(string? value, int maxLength)
    {
        if (value is not { Length: >= 1 } || value.Length > maxLength)
            return false;

        foreach (char c in value)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Invalid identifiers never leave a provider; the service enforces it again. Every known
    /// provider's identifier shape is accepted, so a new provider must extend this union or all
    /// of its entries are dropped as unusable.
    /// </summary>
    public static bool IsUsable(EmoteCatalogEntry entry) =>
        (IsValidBttvEmoteId(entry.Id) || IsValidSevenTvId(entry.Id) || IsValidFfzEmoteId(entry.Id)) &&
        IsValidCode(entry.Code);
}
