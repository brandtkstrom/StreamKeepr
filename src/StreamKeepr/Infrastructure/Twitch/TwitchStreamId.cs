namespace StreamKeepr.Infrastructure.Twitch;

internal static class TwitchStreamId
{
    /// <summary>Stream IDs become session path segments, so accept digits only.</summary>
    public static bool IsNumeric(string? id) =>
        id is { Length: > 0 and <= 20 } && id.All(char.IsAsciiDigit);
}
