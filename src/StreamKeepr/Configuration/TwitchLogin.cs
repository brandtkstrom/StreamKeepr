using System.Text.RegularExpressions;

namespace StreamKeepr.Configuration;

/// <summary>The one authoritative Twitch login normalization and validation rule.</summary>
public static class TwitchLogin
{
    private static readonly Regex Pattern = new(
        "^[a-z0-9_]{1,25}$", RegexOptions.CultureInvariant);

    public static string Normalize(string? login) =>
        (login ?? "").Trim().ToLowerInvariant();

    public static bool IsValid(string login) => Pattern.IsMatch(login);
}
