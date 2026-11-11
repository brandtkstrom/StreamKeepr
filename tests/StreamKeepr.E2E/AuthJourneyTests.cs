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

        ILocator chip = page.Locator("button.sk-auth-chip");
        await chip.WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Twitch account: Mock Admin" }).WaitForAsync();
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "auth-connected");

        await chip.ClickAsync();
        ILocator disconnect = page.GetByRole(AriaRole.Menuitem, new() { Name = "Disconnect" });
        await disconnect.WaitForAsync();
        await disconnect.ClickAsync();

        // The antiforgery form post signs out and returns to the same page and query.
        ILocator connect = page.Locator("a.sk-twitch[data-auth-state='disconnected']");
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

        ILocator snackbar = page.Locator(".sk-snackbar");
        await snackbar.WaitForAsync(new() { Timeout = 15_000 });
        Assert.Contains("This Twitch account is not allowed to manage Stream Keepr.", await snackbar.InnerTextAsync());
        await page.WaitForURLAsync(url => url.EndsWith("/?sort=az", StringComparison.Ordinal));
        Assert.Equal(1, await snackbar.CountAsync());
    }
}
