using Microsoft.AspNetCore.WebUtilities;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Ui;

/// <summary>
/// URL rules for the Twitch sign-in UI (details/auth/settings contract §7): the local return
/// URL, the login link, and the one-shot <c>authError</c> query key.
/// </summary>
public static class AuthLinks
{
    private static readonly Dictionary<string, string> Messages = new(StringComparer.Ordinal)
    {
        [AdminAuth.NotAllowedAuthError] = "This Twitch account is not allowed to manage Stream Keepr.",
        [AdminAuth.HttpsRequiredAuthError] = "Twitch sign-in requires HTTPS except on localhost.",
        [AdminAuth.FailedAuthError] = "Twitch sign-in failed. Try again.",
    };

    /// <summary>The current local path and query, without the fragment or an <c>authError</c>.</summary>
    public static string ReturnUrl(string uri) => WithoutAuthError(uri);

    public static string LoginHref(string uri) =>
        $"{AdminAuth.LoginPath}?returnUrl={Uri.EscapeDataString(ReturnUrl(uri))}";

    /// <summary>The snackbar text for a known <c>authError</c> code, or null.</summary>
    public static string? AuthErrorMessage(string uri)
    {
        foreach ((string key, string? value) in Query(uri))
        {
            if (string.Equals(key, AdminAuth.AuthErrorQueryKey, StringComparison.OrdinalIgnoreCase)
                && value is not null
                && Messages.TryGetValue(value, out string? message))
            {
                return message;
            }
        }

        return null;
    }

    /// <summary>Path plus query with every <c>authError</c> key removed and other keys kept in order.</summary>
    public static string WithoutAuthError(string uri)
    {
        var parameters = Query(uri)
            .Where(pair => !string.Equals(pair.Key, AdminAuth.AuthErrorQueryKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        string path = new Uri(uri, UriKind.Absolute).AbsolutePath;
        return path + QueryString.Create(parameters);
    }

    private static List<KeyValuePair<string, string?>> Query(string uri)
    {
        var pairs = new List<KeyValuePair<string, string?>>();
        foreach ((string key, var values) in QueryHelpers.ParseQuery(new Uri(uri, UriKind.Absolute).Query))
        {
            foreach (string? value in values)
                pairs.Add(new(key, value));
        }

        return pairs;
    }
}
