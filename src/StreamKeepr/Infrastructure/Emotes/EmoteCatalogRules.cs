namespace StreamKeepr.Infrastructure.Emotes;

/// <summary>Validation and size limits shared by provider normalization and aggregation.</summary>
internal static class EmoteCatalogRules
{
    public const int MaxEntriesPerCatalog = 2_000;
    public const int MaxCodeLength = 128;
    public const int MaxResponseBytes = 2 * 1024 * 1024;

    /// <summary>BetterTTV emote IDs are 24 hexadecimal characters.</summary>
    public static bool IsValidEmoteId(string? id)
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

    public static bool IsValidCode(string? code) =>
        !string.IsNullOrEmpty(code) && code.Length <= MaxCodeLength;

    /// <summary>Twitch user IDs are numeric and at most 20 digits.</summary>
    public static bool IsValidChannelId(string? channelId) =>
        channelId is { Length: >= 1 and <= 20 } && channelId.All(char.IsAsciiDigit);

    /// <summary>Modifiers, restricted entries, and invalid identifiers never leave a provider.</summary>
    public static bool IsUsable(EmoteCatalogEntry entry) =>
        !entry.Modifier && IsValidEmoteId(entry.Id) && IsValidCode(entry.Code);
}
