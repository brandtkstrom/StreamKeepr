namespace StreamKeepr.Configuration;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    // Empty means every authenticated Twitch user is an admin; startup logs a warning.
    public List<string> AllowedTwitchLogins
    {
        get;
        set => field = value is null
            ? []
            : value.Select(TwitchLogin.Normalize).ToList();
    } = [];

    public static bool IsValidLogin(string login) => TwitchLogin.IsValid(login);
}
