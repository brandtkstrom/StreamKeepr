using System.Text.RegularExpressions;

namespace StreamKeepr.Configuration;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    private static readonly Regex LoginPattern = new("^[a-z0-9_]{1,25}$");

    // Empty means every authenticated Twitch user is an admin; startup logs a warning.
    public List<string> AllowedTwitchLogins
    {
        get;
        set => field = value is null
            ? []
            : value.Select(login => login?.Trim().ToLowerInvariant() ?? "").ToList();
    } = [];

    public static bool IsValidLogin(string login) => LoginPattern.IsMatch(login);
}
