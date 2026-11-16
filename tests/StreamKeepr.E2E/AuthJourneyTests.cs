using Deque.AxeCore.Commons;
using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

[Collection(MockAppCollection.Name)]
[Trait("Category", "E2E")]
public sealed class AuthJourneyTests(MockAppFixture app)
{
    [Fact]
    public async Task The_fake_admin_signs_out_and_back_in_from_the_top_bar_keeping_the_page()
    {
        await using Session session = await OpenAsync($"{app.BaseUrl}?sort=az");
        IPage page = session.Page;

        // The account is an avatar-only menu activator, named for the account (#174).
        ILocator chip = page.GetByRole(AriaRole.Button, new() { Name = "Twitch account: Mock Admin" });
        await chip.WaitForAsync();
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "auth-connected");

        // The avatar opens its menu from the keyboard too, and the open menu is axe clean.
        await chip.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        ILocator disconnect = page.GetByRole(AriaRole.Menuitem, new() { Name = "Disconnect" });
        await disconnect.WaitForAsync();
        ILocator header = page.Locator("[data-account-header]");
        Assert.Contains("Connected to Twitch", await header.InnerTextAsync());
        // Let the popover finish fading in, or axe measures contrast mid-transition.
        await LibraryJourneyTests.Expect(
            async () => await header.EvaluateAsync<bool>("""
                e => { for (; e; e = e.parentElement) if (getComputedStyle(e).opacity !== "1") return false; return true; }
                """) ? 1 : 0,
            1);
        // MudBlazor renders every popover in its provider at the end of <body>, outside the
        // landmarks, so "region" cannot hold while a menu is open. Every closed-page check keeps it.
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(
            page,
            "auth-account-menu",
            new AxeRunOptions { Rules = new() { ["region"] = new RuleOptions { Enabled = false } } });
        await disconnect.ClickAsync();

        // The antiforgery form post signs out and returns to the same page and query.
        ILocator connect = page.Locator("a[data-auth-state='disconnected']");
        await connect.WaitForAsync(new() { Timeout = 15_000 });
        Assert.EndsWith("/?sort=az", page.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(
            await session.Context.CookiesAsync(),
            cookie => cookie.Name == "sk-auth" && !string.IsNullOrEmpty(cookie.Value));
        Assert.Equal(
            "/api/auth/twitch/login?returnUrl=%2F%3Fsort%3Daz",
            await connect.GetAttributeAsync("href"));
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "auth-disconnected");

        // Signed out survives a reload in the Mock profile until the next sign-in.
        await page.ReloadAsync();
        await connect.WaitForAsync();

        await connect.ClickAsync();
        await chip.WaitForAsync(new() { Timeout = 15_000 });
        Assert.EndsWith("/?sort=az", page.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sign_in_error_shows_one_snackbar_and_leaves_the_other_query_keys()
    {
        await using Session session = await OpenAsync($"{app.BaseUrl}?sort=az&authError=notAllowed");
        IPage page = session.Page;

        ILocator snackbar = page.Locator("[data-snackbar]");
        await snackbar.WaitForAsync(new() { Timeout = 15_000 });
        Assert.Contains("This Twitch account is not allowed to manage Stream Keepr.", await snackbar.InnerTextAsync());
        await page.WaitForURLAsync(url => url.EndsWith("/?sort=az", StringComparison.Ordinal));
        Assert.Equal(1, await snackbar.CountAsync());
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "auth-error");
    }
}
