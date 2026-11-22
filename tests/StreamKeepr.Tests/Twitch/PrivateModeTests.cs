using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Tests.Twitch;

/// <summary>ADR-0013: a non-empty allowlist puts the whole app behind an allowed Twitch sign-in.</summary>
public sealed class PrivateModeTests
{
    [Theory]
    [InlineData("/", "/signin?returnUrl=%2F")]
    [InlineData("/r/20261115T090000Z_1?q=jazz", "/signin?returnUrl=%2Fr%2F20261115T090000Z_1%3Fq%3Djazz")]
    [InlineData("/settings", "/signin?returnUrl=%2Fsettings")]
    [InlineData("/no-such-page", "/signin?returnUrl=%2Fno-such-page")]
    public async Task A_signed_out_page_visit_redirects_to_sign_in_with_the_page_to_return_to(string path, string location)
    {
        using var harness = PrivateHarness();
        using HttpClient client = harness.CreateRequestClient();

        using HttpResponseMessage response = await client.SendAsync(Page(path));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData("GET", "/media/chan/20261115T090000Z_1/index.m3u8")]
    [InlineData("GET", "/media/chan/20261115T090000Z_1/thumb.jpg")]
    [InlineData("GET", "/api/recordings/20261115T090000Z_1/chat")]
    [InlineData("GET", "/api/recordings/20261115T090000Z_1/viewers")]
    [InlineData("GET", "/api/recordings/20261115T090000Z_1/emotes")]
    [InlineData("PUT", "/api/recordings/20261115T090000Z_1/progress")]
    [InlineData("POST", "/_blazor/negotiate?negotiateVersion=1")]
    public async Task Media_the_api_and_the_circuit_answer_401_when_signed_out(string method, string path)
    {
        using var harness = PrivateHarness();
        using HttpClient client = harness.CreateRequestClient();

        // Even a request that says it accepts HTML is not a page here.
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Accept.ParseAdd("text/html");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/signin")]
    [InlineData("/health/live")]
    [InlineData("/css/components.css")]
    [InlineData("/img/streamkeepr-mark.svg")]
    [InlineData("/_framework/blazor.web.js")]
    public async Task The_sign_in_page_health_and_static_assets_stay_public(string path)
    {
        using var harness = PrivateHarness();
        using HttpClient client = harness.CreateRequestClient();

        using HttpResponseMessage response = await client.SendAsync(Page(path));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_login_endpoint_stays_public_and_challenges_twitch()
    {
        using var harness = PrivateHarness();

        SignInResponse signIn = await harness.SignInAsync("/settings");

        Assert.StartsWith("https://id.twitch.tv/oauth2/authorize", signIn.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Equal("/settings", signIn.CallbackLocation);
    }

    [Fact]
    public async Task An_allowed_session_reaches_pages_and_the_api()
    {
        using var harness = PrivateHarness();
        string cookie = await SignInCookieAsync(harness);
        using HttpClient client = harness.CreateRequestClient();

        using HttpResponseMessage page = await client.SendAsync(Page("/", cookie));
        using HttpResponseMessage viewers = await client.SendAsync(
            WithCookie(new HttpRequestMessage(HttpMethod.Get, "/api/recordings/missing/viewers"), cookie));

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, viewers.StatusCode);
    }

    [Fact]
    public async Task A_login_removed_from_the_list_loses_its_session_and_is_told_why()
    {
        using var harness = PrivateHarness();
        string cookie = await SignInCookieAsync(harness);
        harness.Factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value.AllowedTwitchLogins = ["someone_else"];
        using HttpClient client = harness.CreateRequestClient();

        using HttpResponseMessage page = await client.SendAsync(Page("/settings", cookie));
        using HttpResponseMessage media = await client.SendAsync(
            WithCookie(new HttpRequestMessage(HttpMethod.Get, "/api/recordings/missing/viewers"), cookie));

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal(
            "/signin?returnUrl=%2Fsettings&authError=notAllowed&login=allowed_login",
            page.Headers.Location?.ToString());
        Assert.Contains(page.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith($"{AdminAuth.CookieScheme}=;", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, media.StatusCode);
    }

    [Fact]
    public async Task Open_mode_keeps_viewing_public_and_has_no_sign_in_page()
    {
        using var harness = new AdminAuthHarness();
        using HttpClient client = harness.CreateRequestClient();

        using HttpResponseMessage home = await client.SendAsync(Page("/"));
        using HttpResponseMessage signIn = await client.SendAsync(Page("/signin?returnUrl=%2Fsettings"));

        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal(new Uri("https://localhost/settings"), signIn.Headers.Location);
    }

    [Fact]
    public async Task The_sign_in_page_shows_nothing_from_the_library_and_starts_on_the_twitch_button()
    {
        using var harness = PrivateHarness();

        IDocument page = await SignInPageAsync(harness, "/signin?returnUrl=%2Fr%2F20261115T090000Z_1");

        Assert.Equal("Sign in · Stream Keepr", page.Title);
        Assert.Equal("ready", page.QuerySelector("[data-signin-state]")?.GetAttribute("data-signin-state"));
        Assert.Equal("Sign in to watch", page.QuerySelector("h1")?.TextContent.Trim());
        IElement button = page.QuerySelector("a[data-signin-button]")!;
        Assert.Equal("/api/auth/twitch/login?returnUrl=%2Fr%2F20261115T090000Z_1", button.GetAttribute("href"));
        Assert.True(button.HasAttribute("autofocus"));
        Assert.Empty(page.QuerySelectorAll("[data-signin-alert]"));
        // Static: no circuit script, no app bar, drawer or nav.
        Assert.DoesNotContain(page.Scripts, script => script.Source?.Contains("blazor.web", StringComparison.Ordinal) == true);
        Assert.Empty(page.QuerySelectorAll("header, nav, [data-watch]"));
    }

    [Fact]
    public async Task Not_allowed_names_the_login_and_offers_another_account()
    {
        using var harness = PrivateHarness();

        IDocument page = await SignInPageAsync(harness, "/signin?returnUrl=%2F&authError=notAllowed&login=pixelwren");

        IElement alert = page.QuerySelector("[data-signin-alert]")!;
        Assert.Equal("alert", alert.GetAttribute("role"));
        Assert.Contains("pixelwren can't use this StreamKeepr.", alert.TextContent);
        IElement button = page.QuerySelector("a[data-signin-button]")!;
        Assert.Equal("Use another Twitch account", button.TextContent.Trim());
        Assert.Equal("/api/auth/twitch/login?returnUrl=%2F&forceVerify=true", button.GetAttribute("href"));
    }

    [Fact]
    public async Task A_login_that_is_not_a_twitch_login_is_not_echoed_onto_the_page()
    {
        using var harness = PrivateHarness();

        IDocument page = await SignInPageAsync(harness, "/signin?authError=notAllowed&login=%3Cb%3Ehi");

        Assert.StartsWith("This Twitch account can't use", page.QuerySelector("[data-signin-alert]")!.TextContent.Trim(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("authError=expired", "expired", "status", "Your Twitch session expired.")]
    [InlineData("signedOut=true", "signedout", "status", "You're signed out.")]
    [InlineData("authError=failed", "failed", "alert", "Twitch sign-in failed.")]
    [InlineData("authError=httpsRequired", "nohttps", "status", "Twitch sign-in needs HTTPS.")]
    public async Task Each_reason_shows_its_alert(string query, string state, string role, string text)
    {
        using var harness = PrivateHarness();

        IDocument page = await SignInPageAsync(harness, $"/signin?{query}");

        Assert.Equal(state, page.QuerySelector("[data-signin-state]")?.GetAttribute("data-signin-state"));
        IElement alert = page.QuerySelector("[data-signin-alert]")!;
        Assert.Equal(role, alert.GetAttribute("role"));
        Assert.Contains(text, alert.TextContent);
    }

    [Fact]
    public async Task Plain_http_off_localhost_has_no_button_because_sign_in_could_not_succeed()
    {
        using var harness = new AdminAuthHarness(
            overrides: AdminAuthTestSettings.Allowlist("allowed_login"),
            baseAddress: "http://streamkeepr.lan");

        IDocument page = await SignInPageAsync(harness, "/signin", "http://streamkeepr.lan");

        Assert.Equal("nohttps", page.QuerySelector("[data-signin-state]")?.GetAttribute("data-signin-state"));
        Assert.Empty(page.QuerySelectorAll("[data-signin-button]"));
    }

    [Fact]
    public async Task A_signed_in_visitor_goes_straight_back_to_the_return_page()
    {
        using var harness = PrivateHarness();
        string cookie = await SignInCookieAsync(harness);
        using HttpClient client = harness.CreateRequestClient();

        using HttpResponseMessage response = await client.SendAsync(Page("/signin?returnUrl=%2Fsettings", cookie));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(new Uri("https://localhost/settings"), response.Headers.Location);
    }

    [Fact]
    public async Task An_expired_session_ends_its_cookie_on_the_sign_in_page()
    {
        using var harness = PrivateHarness();
        string cookie = await SignInCookieAsync(harness);
        using HttpClient client = harness.CreateRequestClient();

        using HttpResponseMessage response = await client.SendAsync(Page("/signin?authError=expired", cookie));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith($"{AdminAuth.CookieScheme}=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Use_another_account_asks_twitch_for_its_account_prompt()
    {
        using var harness = PrivateHarness();
        using HttpClient client = harness.CreateRequestClient();

        using HttpResponseMessage forced = await client.GetAsync($"{AdminAuth.LoginPath}?returnUrl=%2F&forceVerify=true");
        using HttpResponseMessage plain = await client.GetAsync($"{AdminAuth.LoginPath}?returnUrl=%2F");

        Assert.Equal("true", QueryHelpers.ParseQuery(forced.Headers.Location?.Query)["force_verify"].ToString());
        Assert.Equal("false", QueryHelpers.ParseQuery(plain.Headers.Location?.Query)["force_verify"].ToString());
    }

    [Theory]
    [InlineData("/r/1?authError=failed&q=x", null, "/signin?returnUrl=%2Fr%2F1%3Fq%3Dx")]
    [InlineData("https://evil.example/", null, "/signin?returnUrl=%2F")]
    [InlineData("//evil.example", "failed", "/signin?returnUrl=%2F&authError=failed")]
    [InlineData(null, "expired", "/signin?returnUrl=%2F&authError=expired")]
    public void Sign_in_urls_keep_only_a_local_return_url_without_an_old_error(
        string? returnUrl, string? authError, string expected)
    {
        Assert.Equal(expected, PrivateMode.SignInUrl(returnUrl, authError));
    }

    private static AdminAuthHarness PrivateHarness() =>
        new(overrides: AdminAuthTestSettings.Allowlist("allowed_login"));

    private static async Task<string> SignInCookieAsync(AdminAuthHarness harness)
    {
        SignInResponse signIn = await harness.SignInAsync();
        return $"{AdminAuth.CookieScheme}={Assert.IsType<string>(signIn.AuthCookieValue)}";
    }

    private static async Task<IDocument> SignInPageAsync(
        AdminAuthHarness harness, string path, string baseAddress = "https://localhost")
    {
        using HttpClient client = harness.CreateRequestClient(baseAddress);
        using HttpResponseMessage response = await client.SendAsync(Page(path));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage Page(string path, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("text/html");
        return cookie is null ? request : WithCookie(request, cookie);
    }

    private static HttpRequestMessage WithCookie(HttpRequestMessage request, string cookie)
    {
        request.Headers.Add("Cookie", cookie);
        return request;
    }
}
