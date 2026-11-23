namespace StreamKeepr.Hosting;

/// <summary>
/// The path prefixes that decide how a request is answered: API and media requests and the
/// Blazor circuit get status codes, never a sign-in redirect.
/// </summary>
public static class RequestPaths
{
    /// <summary>Every JSON endpoint and the auth endpoints (<c>/api/auth/...</c>).</summary>
    public const string Api = "/api";

    /// <summary>Recording media, served by <see cref="RecordingsMedia"/> and the live index.</summary>
    public const string Media = "/media";

    /// <summary>The Blazor Server circuit's hub.</summary>
    public const string BlazorHub = "/_blazor";
}
