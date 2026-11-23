using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Twitch;

public sealed class AuthRoutingTests
{
    [Fact]
    public async Task Logout_with_antiforgery_signs_out_clears_the_token_and_redirects_locally()
    {
        using var harness = new AdminAuthHarness(
            overrides: AdminAuthTestSettings.Allowlist("allowed_login"));
        var signIn = await harness.SignInAsync("/settings?tab=twitch");
        var authCookie = Assert.IsType<string>(signIn.AuthCookieValue);
        Assert.NotNull(harness.Store.Token);

        var antiforgery = harness.GetAntiforgeryToken(authCookie);
        using var client = harness.CreateRequestClient();

        using var response = await PostLogoutAsync(
            client, authCookie, antiforgery, "/settings?tab=twitch");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/settings?tab=twitch", response.Headers.Location?.ToString());
        Assert.True(harness.Store.ClearCount > 0);
        Assert.Null(harness.Store.Token);
        Assert.Contains(GetSetCookies(response),
            cookie => cookie.StartsWith($"{AdminAuth.CookieScheme}=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Successful_logout_logs_an_information_audit_with_the_normalized_login()
    {
        using var harness = new AdminAuthHarness(
            login: "Allowed_Login",
            overrides: AdminAuthTestSettings.Allowlist("allowed_login"));
        var signIn = await harness.SignInAsync();
        var authCookie = Assert.IsType<string>(signIn.AuthCookieValue);
        var antiforgery = harness.GetAntiforgeryToken(authCookie);

        using var client = harness.CreateRequestClient();
        using var response = await PostLogoutAsync(client, authCookie, antiforgery, "/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        CapturedLogEntry audit = Assert.Single(
            harness.Factory.Logs.Entries,
            entry => entry.Message.Contains("Admin signed out", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, audit.Level);
        Assert.Equal("allowed_login", audit.Properties["Login"]);
    }

    [Fact]
    public async Task Sign_in_and_logout_never_log_tokens_cookies_oauth_code_or_state()
    {
        using var harness = new AdminAuthHarness(
            overrides: AdminAuthTestSettings.Allowlist("allowed_login"));
        var signIn = await harness.SignInAsync();
        var authCookie = Assert.IsType<string>(signIn.AuthCookieValue);
        var state = QueryHelpers.ParseQuery(new Uri(signIn.AuthorizeUrl).Query)["state"].ToString();
        var antiforgery = harness.GetAntiforgeryToken(authCookie);

        using var client = harness.CreateRequestClient();
        using var response = await PostLogoutAsync(client, authCookie, antiforgery, "/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        string[] secrets =
        [
            TwitchOAuthStub.AccessToken,
            TwitchOAuthStub.RefreshToken,
            "test-code",
            state,
            authCookie,
            antiforgery.RequestToken,
            antiforgery.Cookie.Split('=', 2)[1],
        ];
        Assert.All(secrets, secret => Assert.False(string.IsNullOrEmpty(secret)));

        // Both audits ran, so the scan below covers the sign-in and sign-out log paths.
        Assert.Contains(harness.Factory.Logs.Messages,
            message => message.Contains("Admin signed in", StringComparison.Ordinal));
        Assert.Contains(harness.Factory.Logs.Messages,
            message => message.Contains("Admin signed out", StringComparison.Ordinal));

        foreach (CapturedLogEntry entry in harness.Factory.Logs.Entries)
        {
            string logged = string.Join(
                '\n',
                [entry.Message, entry.Exception?.ToString(), .. entry.Properties.Values.Select(value => value?.ToString())]);
            foreach (string secret in secrets)
                Assert.DoesNotContain(secret, logged, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Logout_without_antiforgery_is_rejected_without_signing_out()
    {
        using var harness = new AdminAuthHarness(
            overrides: AdminAuthTestSettings.Allowlist("allowed_login"));
        var signIn = await harness.SignInAsync();
        var authCookie = Assert.IsType<string>(signIn.AuthCookieValue);

        using var client = harness.CreateRequestClient();
        using var response = await PostLogoutAsync(client, authCookie, null, "/");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(0, harness.Store.ClearCount);
        Assert.NotNull(harness.Store.Token);
        Assert.DoesNotContain(GetSetCookies(response),
            cookie => cookie.StartsWith($"{AdminAuth.CookieScheme}=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logout_with_an_invalid_antiforgery_token_is_rejected()
    {
        using var harness = new AdminAuthHarness(
            overrides: AdminAuthTestSettings.Allowlist("allowed_login"));
        var signIn = await harness.SignInAsync();
        var authCookie = Assert.IsType<string>(signIn.AuthCookieValue);
        var antiforgery = harness.GetAntiforgeryToken(authCookie) with
        {
            RequestToken = "not-a-valid-token",
        };

        using var client = harness.CreateRequestClient();
        using var response = await PostLogoutAsync(client, authCookie, antiforgery, "/");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, harness.Store.ClearCount);
        Assert.NotNull(harness.Store.Token);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("https://evil.example/steal", "/")]
    [InlineData("//evil.example/steal", "/")]
    [InlineData("/r/abc?q=one#player", "/r/abc?q=one#player")]
    public async Task Logout_redirects_only_to_local_urls(string? returnUrl, string expected)
    {
        using var harness = new AdminAuthHarness(
            overrides: AdminAuthTestSettings.Allowlist("allowed_login"));
        var signIn = await harness.SignInAsync();
        var authCookie = Assert.IsType<string>(signIn.AuthCookieValue);
        var antiforgery = harness.GetAntiforgeryToken(authCookie);

        using var client = harness.CreateRequestClient();
        using var response = await PostLogoutAsync(client, authCookie, antiforgery, returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://127.0.0.1")]
    public async Task Login_over_plain_http_is_allowed_on_loopback(string baseAddress)
    {
        using var harness = new AdminAuthHarness(baseAddress: baseAddress);

        using var response = await harness.GetAsync(
            $"{AdminAuth.LoginPath}?returnUrl=%2Fsettings");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(
            "https://id.twitch.tv/oauth2/authorize",
            response.Headers.Location?.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_over_plain_http_outside_loopback_redirects_with_https_required()
    {
        using var harness = new AdminAuthHarness(baseAddress: "http://streamkeepr.lan");

        using var response = await harness.GetAsync(
            $"{AdminAuth.LoginPath}?returnUrl=%2Fsettings%3Ftab%3Dtwitch");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            $"/settings?tab=twitch&{AdminAuth.AuthErrorQueryKey}={AdminAuth.HttpsRequiredAuthError}",
            response.Headers.Location?.ToString());
        Assert.Contains(harness.Factory.Logs.Messages,
            message => message.Contains("Twitch sign-in was refused", StringComparison.Ordinal)
                && message.Contains("HTTPS", StringComparison.Ordinal)
                && message.Contains("ForwardedHeaders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_allowed_sign_in_goes_to_the_sign_in_page_with_the_reason_and_login()
    {
        using var harness = new AdminAuthHarness(
            login: "intruder",
            overrides: AdminAuthTestSettings.Allowlist("allowed_login"));

        var result = await harness.SignInAsync("/settings?tab=twitch");

        // A non-empty allowlist makes the app private (ADR-0013), so the return page would only
        // redirect to the sign-in page; the callback goes there directly with the reason.
        Assert.Equal(HttpStatusCode.Redirect, result.CallbackStatus);
        Assert.Equal(
            "/signin?returnUrl=%2Fsettings%3Ftab%3Dtwitch&authError=notAllowed&login=intruder",
            result.CallbackLocation);
        Assert.Null(result.AuthCookie);
        Assert.Empty(harness.Store.SavedTokens);
    }

    [Fact]
    public async Task Remote_oauth_failure_returns_to_the_local_return_url_with_failed()
    {
        using var harness = new AdminAuthHarness(failTokenExchange: true);

        var result = await harness.SignInAsync("/settings?tab=twitch");

        Assert.Equal(HttpStatusCode.Redirect, result.CallbackStatus);
        Assert.Equal(
            $"/settings?tab=twitch&{AdminAuth.AuthErrorQueryKey}={AdminAuth.FailedAuthError}",
            result.CallbackLocation);
        Assert.Null(result.AuthCookie);
        Assert.Empty(harness.Store.SavedTokens);
    }

    [Fact]
    public async Task Denied_oauth_sign_in_returns_to_the_local_return_url_with_failed()
    {
        using var harness = new AdminAuthHarness();

        var result = await harness.AccessDeniedAsync("/settings?tab=twitch");

        Assert.Equal(HttpStatusCode.Redirect, result.CallbackStatus);
        Assert.Equal(
            $"/settings?tab=twitch&{AdminAuth.AuthErrorQueryKey}={AdminAuth.FailedAuthError}",
            result.CallbackLocation);
        Assert.Null(result.AuthCookie);
        Assert.Empty(harness.Store.SavedTokens);
    }

    [Fact]
    public async Task Token_save_failure_goes_to_the_sign_in_page_with_failed_when_private()
    {
        using var harness = new AdminAuthHarness(
            overrides: AdminAuthTestSettings.Allowlist("allowed_login"));
        harness.Store.SaveFailure = new IOException("The recordings volume is full.");

        var result = await harness.SignInAsync("/settings?tab=twitch");

        Assert.Equal(HttpStatusCode.Redirect, result.CallbackStatus);
        Assert.Equal(
            "/signin?returnUrl=%2Fsettings%3Ftab%3Dtwitch&authError=failed",
            result.CallbackLocation);
        Assert.Null(result.AuthCookie);
        Assert.Empty(harness.Store.SavedTokens);
        Assert.Contains(harness.Factory.Logs.Messages,
            message => message.Contains("could not be saved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Trusted_proxy_forwarded_headers_set_the_public_origin()
    {
        using var harness = new AdminAuthHarness(
            overrides: AdminAuthTestSettings.TrustedProxies("10.0.0.10"),
            baseAddress: "http://streamkeepr.example",
            remoteIpAddress: IPAddress.Parse("10.0.0.10"));

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{AdminAuth.LoginPath}?returnUrl=%2Fsettings");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "streamkeepr.example");

        using var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var authorizeUrl = response.Headers.Location?.ToString();
        Assert.NotNull(authorizeUrl);
        Assert.StartsWith("https://id.twitch.tv/oauth2/authorize", authorizeUrl, StringComparison.Ordinal);

        var query = QueryHelpers.ParseQuery(new Uri(authorizeUrl).Query);
        Assert.Equal("https://streamkeepr.example/signin-twitch", query["redirect_uri"].ToString());
    }

    [Fact]
    public async Task Trusted_proxy_network_forwarded_headers_set_the_public_origin()
    {
        using var harness = new AdminAuthHarness(
            overrides: AdminAuthTestSettings.TrustedNetworks("10.0.0.0/24"),
            baseAddress: "http://streamkeepr.example",
            remoteIpAddress: IPAddress.Parse("10.0.0.10"));

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{AdminAuth.LoginPath}?returnUrl=%2Fsettings");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "streamkeepr.example");

        using var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var authorizeUrl = response.Headers.Location?.ToString();
        Assert.NotNull(authorizeUrl);
        var query = QueryHelpers.ParseQuery(new Uri(authorizeUrl).Query);
        Assert.Equal("https://streamkeepr.example/signin-twitch", query["redirect_uri"].ToString());
    }

    [Fact]
    public async Task Forwarded_headers_from_an_untrusted_client_are_ignored()
    {
        using var harness = new AdminAuthHarness(
            overrides: AdminAuthTestSettings.TrustedProxies("10.0.0.10"),
            baseAddress: "http://streamkeepr.example",
            remoteIpAddress: IPAddress.Parse("10.0.0.11"));

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{AdminAuth.LoginPath}?returnUrl=%2Fsettings");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "streamkeepr.example");

        using var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            $"/settings?{AdminAuth.AuthErrorQueryKey}={AdminAuth.HttpsRequiredAuthError}",
            response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Loopback_is_not_trusted_when_only_a_remote_proxy_is_configured()
    {
        using var harness = new AdminAuthHarness(
            overrides: AdminAuthTestSettings.TrustedProxies("10.0.0.10"),
            baseAddress: "http://streamkeepr.example",
            remoteIpAddress: IPAddress.Loopback);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{AdminAuth.LoginPath}?returnUrl=%2Fsettings");
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            $"/settings?{AdminAuth.AuthErrorQueryKey}={AdminAuth.HttpsRequiredAuthError}",
            response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Unconfigured_forwarded_headers_are_ignored()
    {
        using var harness = new AdminAuthHarness(
            baseAddress: "http://streamkeepr.example",
            remoteIpAddress: IPAddress.Parse("10.0.0.10"));

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{AdminAuth.LoginPath}?returnUrl=%2Fsettings");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "streamkeepr.example");

        using var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            $"/settings?{AdminAuth.AuthErrorQueryKey}={AdminAuth.HttpsRequiredAuthError}",
            response.Headers.Location?.ToString());
    }

    private static async Task<HttpResponseMessage> PostLogoutAsync(
        HttpClient client,
        string authCookie,
        AntiforgeryRequestToken? antiforgery,
        string? returnUrl)
    {
        var form = new Dictionary<string, string>();
        if (antiforgery is not null)
            form[antiforgery.FormFieldName] = antiforgery.RequestToken;
        if (returnUrl is not null)
            form["returnUrl"] = returnUrl;

        using var request = new HttpRequestMessage(HttpMethod.Post, AdminAuth.LogoutPath)
        {
            Content = new FormUrlEncodedContent(form),
        };

        var cookies = new List<string> { $"{AdminAuth.CookieScheme}={authCookie}" };
        if (antiforgery is not null)
            cookies.Add(antiforgery.Cookie);
        request.Headers.Add("Cookie", string.Join("; ", cookies));

        return await client.SendAsync(request);
    }

    private static string[] GetSetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToArray()
            : [];
}
