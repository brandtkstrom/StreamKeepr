using System.Net;
using AspNet.Security.OAuth.Twitch;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Tests.Twitch;

public sealed class TwitchSignInTests
{
    [Fact]
    public void Twitch_handler_uses_the_admin_callback_without_scopes_or_saved_tokens()
    {
        using var harness = new AdminAuthHarness();

        var options = GetTwitchOptions(harness);

        Assert.Equal("/signin-twitch", options.CallbackPath.ToString());
        Assert.Equal("test-client-id", options.ClientId);
        Assert.Equal("test-client-secret", options.ClientSecret);
        Assert.Empty(options.Scope);
        Assert.False(options.SaveTokens);
    }

    [Fact]
    public void Cookie_scheme_uses_the_admin_session_settings()
    {
        using var harness = new AdminAuthHarness();

        var options = GetCookieOptions(harness);

        Assert.Equal("sk-auth", options.Cookie.Name);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
        Assert.Equal(CookieSecurePolicy.SameAsRequest, options.Cookie.SecurePolicy);
        Assert.Equal(TimeSpan.FromDays(7), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
        Assert.Equal("/api/auth/twitch/login", options.LoginPath.ToString());
        Assert.Equal("/api/auth/logout", options.LogoutPath.ToString());
    }

    [Fact]
    public async Task Login_challenges_Twitch_with_the_callback_and_no_scopes()
    {
        using var harness = new AdminAuthHarness();

        var result = await harness.SignInAsync("/settings?tab=twitch");

        Assert.Equal(HttpStatusCode.Redirect, result.ChallengeStatus);
        var query = QueryHelpers.ParseQuery(new Uri(result.AuthorizeUrl).Query);
        Assert.Equal("https://localhost/signin-twitch", query["redirect_uri"].ToString());
        Assert.Equal("test-client-id", query["client_id"].ToString());
        Assert.Equal("code", query["response_type"].ToString());
        Assert.True(string.IsNullOrEmpty(query["scope"].ToString()));
    }

    [Fact]
    public async Task Allowed_sign_in_saves_the_admin_token_and_issues_the_cookie()
    {
        using var harness = new AdminAuthHarness(
            login: "allowed_login",
            overrides: AdminAuthTestSettings.Allowlist("allowed_login"));

        var result = await harness.SignInAsync();

        Assert.Equal(HttpStatusCode.Redirect, result.CallbackStatus);
        Assert.Equal("/", result.CallbackLocation);

        var token = Assert.Single(harness.Store.SavedTokens);
        Assert.Equal("allowed_login", token.Login);
        Assert.Equal(TwitchOAuthStub.AccessToken, token.AccessToken);
        Assert.Equal(TwitchOAuthStub.RefreshToken, token.RefreshToken);
        Assert.Equal(AdminAuthHarness.Start.AddHours(1), token.ExpiresAt);

        Assert.NotNull(result.AuthCookie);
        var attributes = GetCookieAttributes(result.AuthCookie);
        Assert.Contains("httponly", attributes);
        Assert.Contains("samesite=lax", attributes);
        Assert.Contains("secure", attributes);

        var ticket = DecryptTicket(harness, result.AuthCookieValue!);
        Assert.Equal(AdminAuthHarness.Start.AddDays(7), ticket.Properties.ExpiresUtc);

        Assert.DoesNotContain(result.SetCookies,
            cookie => cookie.Contains(TwitchOAuthStub.AccessToken, StringComparison.Ordinal));
        Assert.DoesNotContain(result.SetCookies,
            cookie => cookie.Contains(TwitchOAuthStub.RefreshToken, StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Factory.Logs.Messages,
            message => message.Contains(TwitchOAuthStub.AccessToken, StringComparison.Ordinal)
                || message.Contains(TwitchOAuthStub.RefreshToken, StringComparison.Ordinal));
        Assert.Equal(2, harness.Stub.Requests.Count);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("/settings?tab=twitch", "/settings?tab=twitch")]
    [InlineData("http://localhost/settings", "/")]
    [InlineData("https://evil.example/steal", "/")]
    [InlineData("//evil.example/steal", "/")]
    [InlineData("/\\evil.example/steal", "/")]
    public async Task Sign_in_redirects_only_to_local_return_urls(string? returnUrl, string expected)
    {
        using var harness = new AdminAuthHarness();

        var result = await harness.SignInAsync(returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, result.CallbackStatus);
        Assert.Equal(expected, result.CallbackLocation);
        Assert.NotNull(result.AuthCookie);
    }

    [Fact]
    public async Task Configured_allowlist_rejects_other_logins_without_a_cookie_or_token()
    {
        using var harness = new AdminAuthHarness(
            login: "intruder",
            overrides: AdminAuthTestSettings.Allowlist("allowed_login"));

        var result = await harness.SignInAsync();

        Assert.Equal(HttpStatusCode.Redirect, result.CallbackStatus);
        Assert.Equal(
            $"/?{AdminAuth.AuthErrorQueryKey}={AdminAuth.NotAllowedAuthError}",
            result.CallbackLocation);
        Assert.Null(result.AuthCookie);
        Assert.DoesNotContain(result.SetCookies,
            cookie => cookie.StartsWith($"{AdminAuth.CookieScheme}=", StringComparison.Ordinal));
        Assert.Empty(harness.Store.SavedTokens);
        Assert.Contains(harness.Factory.Logs.Messages,
            message => message.Contains("Rejected a Twitch admin sign-in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Empty_allowlist_admits_any_authenticated_login()
    {
        using var harness = new AdminAuthHarness(login: "someone_else");

        var result = await harness.SignInAsync();

        Assert.Equal(HttpStatusCode.Redirect, result.CallbackStatus);
        Assert.NotNull(result.AuthCookie);
        var token = Assert.Single(harness.Store.SavedTokens);
        Assert.Equal("someone_else", token.Login);
    }

    [Fact]
    public async Task Missing_login_claim_is_rejected()
    {
        using var harness = new AdminAuthHarness(includeLogin: false);

        var result = await harness.SignInAsync();

        Assert.Equal(HttpStatusCode.Redirect, result.CallbackStatus);
        Assert.Equal(
            $"/?{AdminAuth.AuthErrorQueryKey}={AdminAuth.FailedAuthError}",
            result.CallbackLocation);
        Assert.Null(result.AuthCookie);
        Assert.Empty(harness.Store.SavedTokens);
    }

    [Fact]
    public async Task Auth_cookie_slides_after_half_its_lifetime()
    {
        using var harness = new AdminAuthHarness(overrides: AdminAuthTestSettings.Allowlist("allowed_login"));
        var result = await harness.SignInAsync();
        Assert.NotNull(result.AuthCookieValue);

        harness.Clock.Advance(TimeSpan.FromDays(4));
        using var home = await harness.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var renewedCookie = home.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(
                cookie => cookie.StartsWith($"{AdminAuth.CookieScheme}=", StringComparison.Ordinal))
            : null;
        Assert.NotNull(renewedCookie);

        var renewedValue = renewedCookie[(AdminAuth.CookieScheme.Length + 1)..].Split(';')[0];
        var ticket = DecryptTicket(harness, renewedValue);
        Assert.Equal(AdminAuthHarness.Start.AddDays(4).AddDays(7), ticket.Properties.ExpiresUtc);
    }

    private static TwitchAuthenticationOptions GetTwitchOptions(AdminAuthHarness harness) =>
        harness.Factory.Services
            .GetRequiredService<IOptionsMonitor<TwitchAuthenticationOptions>>()
            .Get(TwitchAuthenticationDefaults.AuthenticationScheme);

    private static CookieAuthenticationOptions GetCookieOptions(AdminAuthHarness harness) =>
        harness.Factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AdminAuth.CookieScheme);

    private static string[] GetCookieAttributes(string cookieHeader) =>
        cookieHeader.Split("; ", StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(attribute => attribute.Trim().ToLowerInvariant())
            .ToArray();

    private static AuthenticationTicket DecryptTicket(AdminAuthHarness harness, string cookieValue)
    {
        var ticket = GetCookieOptions(harness).TicketDataFormat.Unprotect(cookieValue);
        Assert.NotNull(ticket);
        return ticket;
    }
}
