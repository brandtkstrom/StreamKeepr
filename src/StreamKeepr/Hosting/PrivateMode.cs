using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Hosting;

/// <summary>
/// The private mode of ADR-0013: a non-empty <c>Auth:AllowedTwitchLogins</c> puts every page,
/// media file, API route and the circuit behind an allowed Twitch sign-in. Pages redirect to
/// <see cref="SignInPath"/>; everything else answers 401. Endpoints that stay public mark
/// themselves with <c>AllowAnonymous</c>. With an empty list nothing here applies.
/// </summary>
public static class PrivateMode
{
    public const string SignInPath = "/signin";
    public const string ReturnUrlQueryKey = "returnUrl";
    public const string LoginQueryKey = "login";
    public const string SignedOutQueryKey = "signedOut";

    /// <summary>The <c>authError</c> the shell sends when the 30-minute revalidation fails.</summary>
    public const string ExpiredAuthError = "expired";

    // Set by the session check when it ends a session whose login left the list.
    private const string NotAllowedLoginItem = "StreamKeepr.PrivateMode.NotAllowedLogin";

    /// <summary>
    /// In private mode, the fallback policy requires a session on the admin cookie whose login is
    /// on the list, for every endpoint and every request served by middleware after
    /// authorization (media). The policy re-reads the cookie itself, so it checks the list too.
    /// </summary>
    public static IServiceCollection AddStreamKeeprPrivateMode(this IServiceCollection services)
    {
        services.AddOptions<AuthorizationOptions>()
            .Configure<IOptionsMonitor<AuthOptions>>((options, auth) =>
            {
                // Private or open is decided at startup; the list itself is read on each request,
                // so a login taken off it loses access without a restart.
                if (!auth.CurrentValue.IsPrivate)
                    return;

                options.FallbackPolicy = new AuthorizationPolicyBuilder(AdminAuth.CookieScheme)
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context => IsAllowed(context.User, auth.CurrentValue))
                    .Build();
            });

        return services;
    }

    /// <summary>
    /// Runs after authentication and before authorization. In private mode a session whose login
    /// is no longer on the list is ended here, so "signed in" always means "allowed" further on.
    /// </summary>
    public static IApplicationBuilder UseStreamKeeprPrivateMode(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            AuthOptions auth = context.RequestServices.GetRequiredService<IOptionsMonitor<AuthOptions>>().CurrentValue;
            if (auth.IsPrivate
                && context.User.Identity is { IsAuthenticated: true, Name: var login }
                && !IsAllowed(context.User, auth))
            {
                await context.SignOutAsync(AdminAuth.CookieScheme);
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
                context.Items[NotAllowedLoginItem] = TwitchLogin.Normalize(login);
            }

            await next(context);
        });

    private static bool IsAllowed(ClaimsPrincipal user, AuthOptions auth) =>
        user.Identity?.Name is { } login
        && AdminAuth.IsLoginAllowed(TwitchLogin.Normalize(login), auth.AllowedTwitchLogins);

    public static bool IsPrivate(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOptionsMonitor<AuthOptions>>().CurrentValue.IsPrivate;

    /// <summary>
    /// A browser navigation to a page, as opposed to media, API, circuit or script requests:
    /// only these are redirected to the sign-in page; the rest answer with a status code.
    /// </summary>
    public static bool IsPageRequest(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && !request.Path.StartsWithSegments("/api")
        && !request.Path.StartsWithSegments("/media")
        && !request.Path.StartsWithSegments("/_blazor")
        && request.Headers.Accept.Any(value => value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>
    /// The sign-in page URL for a challenged page request, carrying the page as the return URL
    /// and the not-allowed reason when the session check just ended a session.
    /// </summary>
    public static string ChallengeRedirectUrl(HttpContext context)
    {
        string returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
        return context.Items[NotAllowedLoginItem] is string login
            ? SignInUrl(returnUrl, AdminAuth.NotAllowedAuthError, login)
            : SignInUrl(returnUrl);
    }

    /// <summary>
    /// <c>/signin</c> with a local return URL (anything else becomes <c>/</c>), and optionally a
    /// sign-in error and the rejected login it names.
    /// </summary>
    public static string SignInUrl(
        string? returnUrl, string? authError = null, string? login = null, bool signedOut = false)
    {
        var parameters = new List<KeyValuePair<string, string?>>
        {
            new(ReturnUrlQueryKey, AdminAuth.ResolveReturnUrl(WithoutAuthError(returnUrl))),
        };
        if (authError is not null)
            parameters.Add(new(AdminAuth.AuthErrorQueryKey, authError));
        if (login is not null && TwitchLogin.IsValid(login))
            parameters.Add(new(LoginQueryKey, login));
        if (signedOut)
            parameters.Add(new(SignedOutQueryKey, "true"));

        return SignInPath + QueryString.Create(parameters);
    }

    // A return URL that already failed once must not carry that error into the next attempt.
    private static string? WithoutAuthError(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) || returnUrl[0] != '/')
            return returnUrl;

        int queryIndex = returnUrl.IndexOf('?');
        if (queryIndex < 0)
            return returnUrl;

        var kept = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(returnUrl[queryIndex..])
            .Where(pair => !string.Equals(pair.Key, AdminAuth.AuthErrorQueryKey, StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value)));
        return returnUrl[..queryIndex] + QueryString.Create(kept);
    }
}
