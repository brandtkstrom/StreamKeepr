using System.Security.Claims;
using AspNet.Security.OAuth.Twitch;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
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

    private const string HomePath = "/";

    private const string RejectedSignInItem = "StreamKeepr.AdminAuth.Rejected";

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
                    OnTicketReceived = StopRejectedSignInAsync,
                };
            });

        return services;
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
            Reject(context, services, "Twitch did not return a login for the authenticated user.");
            return;
        }

        if (!IsLoginAllowed(login, auth.AllowedTwitchLogins))
        {
            Reject(context, services, "The Twitch login is not in Auth:AllowedTwitchLogins.");
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
                "Twitch did not return the access token, refresh token, and expiry required for EventSub.");
            return;
        }

        var clock = services.GetRequiredService<TimeProvider>();
        var token = new AdminToken(
            login.ToLowerInvariant(),
            accessToken,
            refreshToken,
            clock.GetUtcNow().Add(expiresIn));

        await services.GetRequiredService<IAdminTokenStore>()
            .SaveAsync(token, context.HttpContext.RequestAborted);
    }

    private static void Reject(
        OAuthCreatingTicketContext context, IServiceProvider services, string reason)
    {
        context.Fail(reason);
        context.Properties.Items[RejectedSignInItem] = "1";

        // The Twitch handler returns a ticket even after context.Fail sets its result,
        // so the ticket-received event must stop the cookie from being issued.
        services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(AdminAuth))
            .LogWarning("Rejected a Twitch admin sign-in: {Reason}", reason);
    }

    private static Task StopRejectedSignInAsync(TicketReceivedContext context)
    {
        if (context.Properties?.Items.Remove(RejectedSignInItem) != true)
            return Task.CompletedTask;

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.HandleResponse();
        return Task.CompletedTask;
    }
}
