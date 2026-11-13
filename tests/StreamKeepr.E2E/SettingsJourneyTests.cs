using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

[Collection(MockAppCollection.Name)]
[Trait("Category", "E2E")]
public sealed class SettingsJourneyTests(MockAppFixture app)
{
    [Fact]
    public async Task An_admin_adds_and_removes_a_channel_and_errors_show_inline()
    {
        await using Session session = await OpenAsync($"{app.BaseUrl}/settings");
        IPage page = session.Page;

        ILocator rows = page.Locator("[data-channel]");
        await rows.First.WaitForAsync();
        Assert.True(await rows.CountAsync() >= 4, "the seeded channels are listed");
        Assert.Equal(0, await page.Locator("[data-channel='mock_wren']").CountAsync());
        Assert.Equal("Mock", await page.Locator("[data-transport]").InnerTextAsync());
        Assert.Equal(
            "Connected",
            await page.Locator("[data-connection]").GetAttributeAsync("data-connection"));
        Assert.Equal("full", await page.Locator("[data-coverage]").GetAttributeAsync("data-coverage"));
        Assert.Equal(
            await rows.CountAsync(),
            await page.Locator("[data-push='connected']").CountAsync());
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "settings");

        ILocator input = page.Locator("[data-channel-login]");
        ILocator add = page.Locator("[data-channel-add]");

        // An unknown login is an inline alert and the input keeps its value.
        await input.FillAsync("mock_nobody");
        await add.ClickAsync();
        ILocator error = page.Locator("[data-channel-add-error]");
        await error.WaitForAsync();
        Assert.Contains("No Twitch channel is named", await error.InnerTextAsync());
        Assert.Equal("mock_nobody", await input.InputValueAsync());

        // A duplicate is refused too.
        await input.FillAsync("mock_nova");
        await input.PressAsync("Enter");
        await page.WaitForFunctionAsync(
            "() => document.querySelector('[data-channel-add-error]')?.textContent.includes('already watched')");

        // Trimmed and lowercased; success clears the input and keeps focus there.
        await input.FillAsync("  Mock_Wren ");
        await add.ClickAsync();
        ILocator wren = page.Locator("[data-channel='mock_wren']");
        await wren.WaitForAsync();
        Assert.Contains("Wren Field", await wren.InnerTextAsync());
        Assert.Equal("", await input.InputValueAsync());
        Assert.True(await input.EvaluateAsync<bool>("i => i === document.activeElement"));
        await page.Locator(".sk-snackbar", new() { HasTextString = "Channel added" }).WaitForAsync();

        await wren.Locator("[data-channel-remove]").ClickAsync();
        ILocator dialog = page.Locator("[data-channel-remove-dialog]");
        await dialog.WaitForAsync();
        Assert.Contains("Recordings are kept", await dialog.InnerTextAsync());
        await page.Locator("[data-channel-remove-confirm]").ClickAsync();

        await wren.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 45_000 });
        await page.Locator(".sk-snackbar", new() { HasTextString = "Channel removed" }).WaitForAsync();
    }

    [Fact]
    public async Task Signed_out_settings_shows_the_connect_prompt_without_channel_data()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;

        // Sign out through the top bar, then open Settings.
        await page.Locator("button.sk-auth-chip").ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Disconnect" }).ClickAsync();
        await page.Locator("a.sk-twitch[data-auth-state='disconnected']").WaitForAsync(new() { Timeout = 15_000 });
        await page.GotoAsync($"{app.BaseUrl}/settings");

        ILocator prompt = page.Locator("[data-settings-signed-out]");
        await prompt.WaitForAsync();
        Assert.Contains("Connect Twitch to manage channels", await prompt.InnerTextAsync());
        Assert.Equal(0, await page.Locator("[data-channel]").CountAsync());
        Assert.Equal(
            "/api/auth/twitch/login?returnUrl=%2Fsettings",
            await prompt.Locator("a.sk-twitch").GetAttributeAsync("href"));
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "settings-signed-out");
    }

    [Fact]
    public async Task An_admin_pauses_and_resumes_a_channel_and_the_library_shows_paused()
    {
        await using Session session = await OpenAsync($"{app.BaseUrl}/settings");
        IPage page = session.Page;

        ILocator row = page.Locator("[data-channel='mock_nova']");
        await row.WaitForAsync();
        await row.Locator("[data-channel-stop]").ClickAsync();

        ILocator dialog = page.Locator("[data-channel-stop-dialog][open]");
        await dialog.WaitForAsync();
        Assert.Contains("stays paused", await dialog.InnerTextAsync());
        await page.Locator("[data-channel-stop-confirm]").ClickAsync();

        await row.Locator("[data-channel-resume]").WaitForAsync();
        Assert.Contains("PAUSED", await row.Locator("[data-channel-state]").InnerTextAsync());
        await page.Locator(".sk-snackbar", new() { HasTextString = "Channel paused" }).WaitForAsync();
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "settings-paused");

        await page.GotoAsync(app.BaseUrl);
        await page.Locator("nav#sk-drawer .sk-group").First.WaitForAsync();
        Assert.Contains(
            "Paused",
            await page.Locator("[data-tree-key='g:mock_nova'] .meta").TextContentAsync());
        Assert.Equal(
            0,
            await page.Locator("[data-tree-key='g:mock_nova'] .sk-live-ring").CountAsync());

        await page.GotoAsync($"{app.BaseUrl}/settings");
        row = page.Locator("[data-channel='mock_nova']");
        await row.Locator("[data-channel-resume]").WaitForAsync();
        await row.Locator("[data-channel-resume]").ClickAsync();
        await row.Locator("[data-channel-stop]").WaitForAsync();
        await page.Locator(".sk-snackbar", new() { HasTextString = "Recording resumed" }).WaitForAsync();
        Assert.Contains("WATCHING", await row.Locator("[data-channel-state]").InnerTextAsync());
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "settings-resumed");
    }
}
