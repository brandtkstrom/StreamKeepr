using System.Security.Claims;
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
                options.LoginPath = "/";
                options.LogoutPath = "/";
            });

        return services;
    }

    /// <summary>
    /// Applies the configured fake identity after authentication and before authorization.
    /// Switching to signed-out also clears a cookie left by an earlier signed-in run.
    /// </summary>
    public static IApplicationBuilder UseStreamKeeprMockAuth(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            MockAuthOptions options = context.RequestServices
                .GetRequiredService<IOptions<MockAuthOptions>>().Value;

            if (options.SignedIn)
            {
                if (context.User.Identity?.IsAuthenticated != true)
                {
                    var principal = CreatePrincipal(options);
                    context.User = principal;
                    await context.SignInAsync(AdminAuth.CookieScheme, principal);
                }
            }
            else if (context.User.Identity?.IsAuthenticated == true)
            {
                await context.SignOutAsync(AdminAuth.CookieScheme);
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
            }

            await next(context);
        });

    private static ClaimsPrincipal CreatePrincipal(MockAuthOptions options)
    {
        string login = TwitchLogin.Normalize(options.Login);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, login),
                new Claim(ClaimTypes.NameIdentifier, login),
                new Claim("display_name", options.DisplayName),
            ],
            authenticationType: AdminAuth.CookieScheme);

        return new ClaimsPrincipal(identity);
    }
}
