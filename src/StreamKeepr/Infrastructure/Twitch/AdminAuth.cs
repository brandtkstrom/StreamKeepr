using System.Net;
using System.Security.Claims;
using AspNet.Security.OAuth.Twitch;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// Configures admin sign-in: the Twitch OAuth handler, the <c>sk-auth</c> cookie,
/// and the allowlist/token-save policy applied when Twitch returns a ticket.
/// </summary>
public static class AdminAuth
{
    public const string CookieScheme = "sk-auth";
    public const string CallbackPath = "/signin-twitch";
    public const string LoginPath = "/api/auth/twitch/login";
    public const string LogoutPath = "/api/auth/logout";

    public const string AuthErrorQueryKey = "authError";
    public const string NotAllowedAuthError = "notAllowed";
    public const string HttpsRequiredAuthError = "httpsRequired";
    public const string FailedAuthError = "failed";

    /// <summary>The display-name claim the Twitch handler maps; the Mock identity uses it too.</summary>
    public const string DisplayNameClaim = TwitchAuthenticationConstants.Claims.DisplayName;

    private const string HomePath = "/";

    private const string RejectedSignInItem = "StreamKeepr.AdminAuth.AuthError";

    public static IServiceCollection AddStreamKeeprAdminAuth(this IServiceCollection services)
    {
        services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieScheme;
                options.DefaultSignInScheme = CookieScheme;
                options.DefaultChallengeScheme = TwitchAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = CookieScheme;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromDays(7);
                options.SlidingExpiration = true;
                options.LoginPath = LoginPath;
                options.LogoutPath = LogoutPath;
                options.Events.OnRedirectToLogin = ChallengeApiWithStatus(StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = ChallengeApiWithStatus(StatusCodes.Status403Forbidden);
            })
            .AddTwitch();

        services.AddOptions<TwitchAuthenticationOptions>(TwitchAuthenticationDefaults.AuthenticationScheme)
            .Configure<IOptions<TwitchOptions>>((options, twitchOptions) =>
            {
                var twitch = twitchOptions.Value;
                options.ClientId = twitch.ClientId;
                options.ClientSecret = twitch.ClientSecret;
                options.CallbackPath = CallbackPath;
                options.Scope.Clear();
                options.SaveTokens = false;
                options.Events = new OAuthEvents
                {
                    OnCreatingTicket = CreateAdminTicketAsync,
                    OnTicketReceived = RedirectRejectedSignInAsync,
                    OnRemoteFailure = RedirectFailedSignInAsync,
                    OnAccessDenied = RedirectDeniedSignInAsync,
                };
            });

        return services;
    }

    /// <summary>
    /// API endpoints answer a missing or insufficient sign-in with a status code; pages keep
    /// the cookie handler's redirect to the login endpoint.
    /// </summary>
    public static Func<RedirectContext<CookieAuthenticationOptions>, Task> ChallengeApiWithStatus(int status) =>
        context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
                context.Response.StatusCode = status;
            else
                context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };

    /// <summary>
    /// Starts sign-in. Plain HTTP is accepted only on localhost/loopback; other
    /// hosts get a local redirect carrying <c>authError=httpsRequired</c> so the
    /// operator sees the HTTPS/forwarded-header requirement instead of a broken
    /// Twitch round trip. <see cref="HttpRequest.IsHttps"/> already reflects
    /// trusted <c>X-Forwarded-Proto</c> values.
    /// </summary>
    public static IResult HandleLogin(HttpContext context, string? returnUrl)
    {
        var localReturnUrl = ResolveReturnUrl(returnUrl);

        if (!context.Request.IsHttps && !IsLocalHost(context.Request.Host))
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(AdminAuth))
                .LogWarning(
                    "Twitch sign-in was refused because the request is not HTTPS and the host " +
                    "is not localhost. Terminate TLS at the reverse proxy and trust it through " +
                    "{Section}:KnownProxies or KnownNetworks.",
                    ForwardedHeadersSettings.SectionName);

            return Results.LocalRedirect(
                AuthErrorRedirectUrl(localReturnUrl, HttpsRequiredAuthError));
        }

        return ChallengeLogin(localReturnUrl);
    }

    /// <summary>
    /// Ends the cookie session and clears the stored admin token, then redirects
    /// to a local URL. Returns 400 before changing anything when the antiforgery
    /// token is missing or invalid.
    /// </summary>
    public static async Task<IResult> HandleLogoutAsync(
        HttpContext context, IAntiforgery antiforgery, IAdminTokenStore tokenStore)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest();
        }

        var returnUrl = context.Request.HasFormContentType
            ? (await context.Request.ReadFormAsync(context.RequestAborted))["returnUrl"].ToString()
            : null;

        // Signing out must not depend on the browser keeping the connection open.
        await tokenStore.ClearAsync(CancellationToken.None);
        await context.SignOutAsync(CookieScheme);

        return Results.LocalRedirect(ResolveReturnUrl(returnUrl));
    }

    /// <summary>
    /// Challenges the Twitch handler, returning to <paramref name="returnUrl"/> only
    /// when it is local; every other value falls back to the home page.
    /// </summary>
    public static IResult ChallengeLogin(string? returnUrl) =>
        Results.Challenge(
            new AuthenticationProperties { RedirectUri = ResolveReturnUrl(returnUrl) },
            [TwitchAuthenticationDefaults.AuthenticationScheme]);

    /// <summary>
    /// True for <c>localhost</c> and loopback IP literals, where Twitch permits a
    /// plain-HTTP redirect URI.
    /// </summary>
    public static bool IsLocalHost(HostString host)
    {
        var hostName = host.Host;
        if (hostName.Length > 1 && hostName[0] == '[' && hostName[^1] == ']')
            hostName = hostName[1..^1];

        return string.Equals(hostName, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(hostName, out var address) && IPAddress.IsLoopback(address));
    }

    /// <summary>
    /// Adds or replaces exactly one <c>authError</c> query value while keeping the
    /// path, other query values, and fragment. External return URLs fall back to
    /// <c>/</c>, matching <see cref="ResolveReturnUrl"/>.
    /// </summary>
    public static string AuthErrorRedirectUrl(string? returnUrl, string authError)
    {
        ArgumentException.ThrowIfNullOrEmpty(authError);

        var local = ResolveReturnUrl(returnUrl);
        var fragmentIndex = local.IndexOf('#');
        var fragment = fragmentIndex < 0 ? string.Empty : local[fragmentIndex..];
        var withoutFragment = fragmentIndex < 0 ? local : local[..fragmentIndex];

        var queryIndex = withoutFragment.IndexOf('?');
        var path = queryIndex < 0 ? withoutFragment : withoutFragment[..queryIndex];
        var query = queryIndex < 0 ? string.Empty : withoutFragment[(queryIndex + 1)..];

        var parameters = new List<KeyValuePair<string, string?>>();
        foreach (var (key, values) in QueryHelpers.ParseQuery(query))
        {
            if (string.Equals(key, AuthErrorQueryKey, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var value in values)
                parameters.Add(new(key, value));
        }

        parameters.Add(new(AuthErrorQueryKey, authError));

        return path + QueryString.Create(parameters) + fragment;
    }

    /// <summary>
    /// Returns <paramref name="returnUrl"/> when it is a local URL; otherwise <c>/</c>.
    /// Mirrors the framework's open-redirect guard used by <c>Results.LocalRedirect</c>
    /// and cookie authentication, because the minimal-API endpoint has no URL helper.
    /// </summary>
    public static string ResolveReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl))
            return HomePath;

        // Allows "/" or "/foo" but not "//" or "/\".
        if (returnUrl[0] == '/')
        {
            if (returnUrl.Length == 1)
                return HomePath;
            if (returnUrl[1] is '/' or '\\' || HasControlCharacter(returnUrl.AsSpan(1)))
                return HomePath;
            return returnUrl;
        }

        // "~/" is local for the framework but is not a usable challenge redirect URI.
        if (returnUrl[0] == '~' && returnUrl.Length > 1 && returnUrl[1] == '/')
        {
            if (returnUrl.Length == 2)
                return HomePath;
            if (returnUrl[2] is '/' or '\\' || HasControlCharacter(returnUrl.AsSpan(2)))
                return HomePath;
            return returnUrl[1..];
        }

        return HomePath;
    }

    /// <summary>
    /// An empty allowlist admits every authenticated Twitch user; otherwise only
    /// the configured logins are admitted.
    /// </summary>
    public static bool IsLoginAllowed(string login, IReadOnlyCollection<string> allowedLogins) =>
        allowedLogins.Count == 0
        || allowedLogins.Contains(login, StringComparer.OrdinalIgnoreCase);

    private static bool HasControlCharacter(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character))
                return true;
        }

        return false;
    }

    private static async Task CreateAdminTicketAsync(OAuthCreatingTicketContext context)
    {
        var services = context.HttpContext.RequestServices;
        var auth = services.GetRequiredService<IOptions<AuthOptions>>().Value;

        // The Twitch handler already called /helix/users with the app's client id and
        // mapped that response's login to ClaimTypes.Name before this event runs.
        var login = context.Principal?.FindFirst(ClaimTypes.Name)?.Value;
        if (string.IsNullOrWhiteSpace(login))
        {
            Reject(
                context,
                services,
                FailedAuthError,
                "Twitch did not return a login for the authenticated user.");
            return;
        }

        if (!IsLoginAllowed(login, auth.AllowedTwitchLogins))
        {
            Reject(
                context,
                services,
                NotAllowedAuthError,
                "The Twitch login is not in Auth:AllowedTwitchLogins.");
            return;
        }

        if (context.AccessToken is not { Length: > 0 } accessToken
            || context.RefreshToken is not { Length: > 0 } refreshToken
            || context.ExpiresIn is not { } expiresIn
            || expiresIn <= TimeSpan.Zero)
        {
            Reject(
                context,
                services,
                FailedAuthError,
                "Twitch did not return the access token, refresh token, and expiry required for EventSub.");
            return;
        }

        var clock = services.GetRequiredService<TimeProvider>();
        var token = new AdminToken(
            login.ToLowerInvariant(),
            accessToken,
            refreshToken,
            clock.GetUtcNow().Add(expiresIn));

        try
        {
            await services.GetRequiredService<IAdminTokenStore>()
                .SaveAsync(token, context.HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            // A failed token save must not leave the user with an admin cookie that
            // cannot drive WebSocket-mode EventSub. The reason is logged and the sign-in is redirected
            // to the local return URL as a generic failure.
            Reject(
                context,
                services,
                FailedAuthError,
                "The Twitch admin token could not be saved.",
                ex);
        }
    }

    private static void Reject(
        OAuthCreatingTicketContext context,
        IServiceProvider services,
        string authError,
        string reason,
        Exception? exception = null)
    {
        context.Fail(reason);
        context.Properties.Items[RejectedSignInItem] = authError;

        // The Twitch handler returns a ticket even after context.Fail sets its result,
        // so the ticket-received event must stop the cookie from being issued.
        services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(AdminAuth))
            .LogWarning(exception, "Rejected a Twitch admin sign-in: {Reason}", reason);
    }

    private static Task RedirectRejectedSignInAsync(TicketReceivedContext context)
    {
        if (context.Properties is null
            || !context.Properties.Items.TryGetValue(RejectedSignInItem, out var authError))
        {
            return Task.CompletedTask;
        }

        context.Properties.Items.Remove(RejectedSignInItem);

        // RemoteAuthenticationHandler moves the challenge RedirectUri into ReturnUri
        // and clears Properties.RedirectUri before TicketReceived runs.
        context.HttpContext.Response.Redirect(
            AuthErrorRedirectUrl(context.ReturnUri, authError ?? FailedAuthError));
        context.HandleResponse();
        return Task.CompletedTask;
    }

    private static Task RedirectFailedSignInAsync(RemoteFailureContext context)
    {
        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(AdminAuth))
            .LogWarning(
                "Twitch admin sign-in failed: {Reason}",
                context.Failure?.Message ?? "The OAuth response was not usable.");

        context.HttpContext.Response.Redirect(
            AuthErrorRedirectUrl(context.Properties?.RedirectUri, FailedAuthError));
        context.HandleResponse();
        return Task.CompletedTask;
    }

    private static Task RedirectDeniedSignInAsync(AccessDeniedContext context)
    {
        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(AdminAuth))
            .LogWarning("Twitch admin sign-in was denied.");

        context.HttpContext.Response.Redirect(
            AuthErrorRedirectUrl(context.Properties?.RedirectUri, FailedAuthError));
        context.HandleResponse();
        return Task.CompletedTask;
    }
}
