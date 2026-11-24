using Microsoft.Playwright;

namespace StreamKeepr.E2E;

/// <summary>
/// ADR-0013 in the browser: with an allowlist the Mock app is private, so a visitor meets the
/// sign-in page, signs in back to the page they asked for, and Sign out returns them to it.
/// Each app has its own process because the allowlist is fixed at startup.
/// </summary>
[CaptureFailure]
[Trait("Category", "E2E")]
public sealed class PrivateModeJourneyTests : IAsyncLifetime
{
    // The fake admin (mock_admin) is on this list, and starts signed out.
    private readonly MockAppFixture _allowed = new(
        "private-allowed",
        new Dictionary<string, string>
        {
            ["Auth__AllowedTwitchLogins__0"] = "mock_admin",
            ["Mock__Auth__SignedIn"] = "false",
        });

    // The fake admin signs in as a login that is not on this list.
    private readonly MockAppFixture _rejected = new(
        "private-rejected",
        new Dictionary<string, string>
        {
            ["Auth__AllowedTwitchLogins__0"] = "someone_else",
            ["Mock__Auth__SignedIn"] = "false",
            ["Mock__Auth__Login"] = "pixelwren",
        });

    public Task InitializeAsync() => Task.WhenAll(_allowed.InitializeAsync(), _rejected.InitializeAsync());

    public async Task DisposeAsync()
    {
        await _allowed.DisposeAsync();
        await _rejected.DisposeAsync();
    }

    [Fact]
    public async Task A_visitor_signs_in_to_the_page_they_asked_for_and_signs_out_to_the_sign_in_page()
    {
        await using Journey session = await OpenAsync(_allowed, "/settings");
        IPage page = session.Page;

        // The deep link lands on the sign-in page, which names it as the return page.
        await Journey.WaitForUrlAsync(page, url => url.Contains("/signin?returnUrl=%2Fsettings", StringComparison.Ordinal));
        Assert.Equal("Sign in · Stream Keepr", await page.TitleAsync());
        ILocator button = page.GetByRole(AriaRole.Link, new() { Name = "Sign in with Twitch" });
        Assert.True(await button.EvaluateAsync<bool>("el => el === document.activeElement"));
        Assert.Equal(0, await page.Locator("header, nav, [data-library-drawer]").CountAsync());
        await new LibraryJourneyTests(_allowed).AssertAxeCleanAsync(page, "signin-ready");

        await button.ClickAsync();

        // Back on the page asked for, with a live circuit and the account signed in.
        await Journey.WaitForUrlAsync(page, url => new Uri(url).AbsolutePath == "/settings", 15_000);
        ILocator account = page.GetByRole(AriaRole.Button, new() { Name = "Twitch account: Mock Admin" });
        await account.WaitForAsync(new() { Timeout = 15_000 });
        // The menu opens through the circuit, so wait until the page is interactive.
        await page.Locator("input[data-search-input][data-sk-ready]")
            .WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 });

        await account.ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Sign out" }).ClickAsync();

        await Journey.WaitForUrlAsync(page, url => url.Contains("/signin?", StringComparison.Ordinal), 15_000);
        ILocator alert = page.Locator("[data-signin-alert]");
        Assert.Equal("You're signed out.", (await alert.InnerTextAsync()).Trim());
        Assert.Equal("status", await alert.GetAttributeAsync("role"));
        await new LibraryJourneyTests(_allowed).AssertAxeCleanAsync(page, "signin-signed-out");

        // Signed out really is out: the library page sends the visitor back here.
        await session.GotoAsync(_allowed.BaseUrl, JourneyReady.Shell);
        await Journey.WaitForUrlAsync(page, url => url.Contains("/signin?returnUrl=%2F", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_login_not_on_the_list_is_named_and_offered_another_account()
    {
        await using Journey session = await OpenAsync(_rejected, "/");
        IPage page = session.Page;
        await Journey.WaitForUrlAsync(page, url => url.Contains("/signin?", StringComparison.Ordinal));

        await page.GetByRole(AriaRole.Link, new() { Name = "Sign in with Twitch" }).ClickAsync();

        ILocator alert = page.GetByRole(AriaRole.Alert);
        await alert.WaitForAsync(new() { Timeout = 15_000 });
        Assert.Contains("pixelwren can't use this StreamKeepr.", await alert.InnerTextAsync());
        ILocator another = page.GetByRole(AriaRole.Link, new() { Name = "Use another Twitch account" });
        Assert.Contains("forceVerify=true", await another.GetAttributeAsync("href"));
        Assert.DoesNotContain(
            await session.Context.CookiesAsync(),
            cookie => cookie.Name == "sk-auth" && !string.IsNullOrEmpty(cookie.Value));
        await new LibraryJourneyTests(_rejected).AssertAxeCleanAsync(page, "signin-not-allowed");
    }

    private static Task<Journey> OpenAsync(MockAppFixture app, string path) =>
        Journey.OpenAsync(
            app,
            new Uri(new Uri(app.BaseUrl), path).ToString(),
            ready: JourneyReady.Shell);
}
