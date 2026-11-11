using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Mock;

/// <summary>Fake admin identity for the Mock profile. No OAuth handler is registered.</summary>
public sealed class MockAuthOptions
{
    public const string SectionName = "Mock:Auth";

    /// <summary>
    /// Signed in by default. Set <c>Mock:Auth:SignedIn</c> (or
    /// <c>Mock__Auth__SignedIn=false</c>) to launch signed out for E2E.
    /// </summary>
    public bool SignedIn { get; set; } = true;

    public string Login { get; set; } = MockCatalog.AdminLogin;

    public string DisplayName { get; set; } = MockCatalog.AdminDisplayName;
}

/// <summary>
/// Cookie-only authentication for the Mock profile. It reuses the production cookie name and
/// authorization pipeline, but the principal comes from configuration instead of Twitch OAuth.
/// </summary>
public static class MockAuth
{
    /// <summary>Set by the fake logout so the default sign-in does not undo it.</summary>
    public const string SignedOutCookie = "sk-mock-signed-out";

    private const string RunClaim = "sk_mock_run";

    // Identifies cookies issued by this process, so a signed-out launch can still clear a
    // cookie left by an earlier signed-in run while keeping one from the fake login.
    private static readonly string RunId = Guid.NewGuid().ToString("N");

    public static IServiceCollection AddStreamKeeprMockAuth(this IServiceCollection services)
    {
        services.AddOptions<MockAuthOptions>()
            .BindConfiguration(MockAuthOptions.SectionName)
            .Validate(
                options => AuthOptions.IsValidLogin(TwitchLogin.Normalize(options.Login)),
                $"{MockAuthOptions.SectionName}:{nameof(MockAuthOptions.Login)} must be a valid Twitch login.")
            .ValidateOnStart();

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = AdminAuth.CookieScheme;
                options.DefaultSignInScheme = AdminAuth.CookieScheme;
                options.DefaultChallengeScheme = AdminAuth.CookieScheme;
            })
            .AddCookie(AdminAuth.CookieScheme, options =>
            {
                options.Cookie.Name = AdminAuth.CookieScheme;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromDays(7);
                options.SlidingExpiration = true;
                options.LoginPath = AdminAuth.LoginPath;
                options.LogoutPath = AdminAuth.LogoutPath;
                options.Events.OnRedirectToLogin = AdminAuth.ChallengeApiWithStatus(StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = AdminAuth.ChallengeApiWithStatus(StatusCodes.Status403Forbidden);
            });

        return services;
    }

    /// <summary>
    /// Applies the configured fake identity after authentication and before authorization.
    /// Signed in by default, unless the fake logout marked this browser signed out. A
    /// signed-out launch clears a cookie left by an earlier run but keeps a fake login's.
    /// </summary>
    public static IApplicationBuilder UseStreamKeeprMockAuth(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            MockAuthOptions options = context.RequestServices
                .GetRequiredService<IOptions<MockAuthOptions>>().Value;
            bool authenticated = context.User.Identity?.IsAuthenticated == true;

            if (options.SignedIn)
            {
                if (!authenticated && !context.Request.Cookies.ContainsKey(SignedOutCookie))
                    await SignInAsync(context, options);
            }
            else if (authenticated && context.User.FindFirst(RunClaim)?.Value != RunId)
            {
                await context.SignOutAsync(AdminAuth.CookieScheme);
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
            }

            await next(context);
        });

    /// <summary>
    /// The Mock profile's stand-ins for the Twitch OAuth endpoints, on the same paths: login
    /// signs the fake admin in and returns to the local URL; logout is the same antiforgery
    /// form post as production and keeps the browser signed out until the next login.
    /// </summary>
    public static IEndpointRouteBuilder MapStreamKeeprMockAuth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(AdminAuth.LoginPath, async (HttpContext context, string? returnUrl) =>
        {
            MockAuthOptions options = context.RequestServices
                .GetRequiredService<IOptions<MockAuthOptions>>().Value;
            context.Response.Cookies.Delete(SignedOutCookie);
            await SignInAsync(context, options);
            return Results.LocalRedirect(AdminAuth.ResolveReturnUrl(returnUrl));
        });

        endpoints.MapPost(AdminAuth.LogoutPath, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            string? returnUrl = context.Request.HasFormContentType
                ? (await context.Request.ReadFormAsync(context.RequestAborted))["returnUrl"].ToString()
                : null;

            await context.SignOutAsync(AdminAuth.CookieScheme);
            context.Response.Cookies.Append(SignedOutCookie, "1", new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps,
                IsEssential = true,
            });
            return Results.LocalRedirect(AdminAuth.ResolveReturnUrl(returnUrl));
        });

        return endpoints;
    }

    private static async Task SignInAsync(HttpContext context, MockAuthOptions options)
    {
        ClaimsPrincipal principal = CreatePrincipal(options);
        context.User = principal;
        await context.SignInAsync(AdminAuth.CookieScheme, principal);
    }

    private static ClaimsPrincipal CreatePrincipal(MockAuthOptions options)
    {
        string login = TwitchLogin.Normalize(options.Login);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, login),
                new Claim(ClaimTypes.NameIdentifier, login),
                new Claim(AdminAuth.DisplayNameClaim, options.DisplayName),
                new Claim(RunClaim, RunId),
            ],
            authenticationType: AdminAuth.CookieScheme);

        return new ClaimsPrincipal(identity);
    }
}
