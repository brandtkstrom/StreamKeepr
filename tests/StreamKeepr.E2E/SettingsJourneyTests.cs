using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

[Collection(MockAppCollection.Name)]
[CaptureFailure]
[Trait("Category", "E2E")]
public sealed class SettingsJourneyTests(MockAppFixture app)
{
    [Fact]
    public async Task An_admin_adds_and_removes_a_channel_and_errors_show_inline()
    {
        await using Journey session = await OpenAsync(app, "/settings");
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
        await Feedback.Snackbar(page, "Channel added").WaitForAsync();

        await wren.Locator("[data-channel-remove]").ClickAsync();
        ILocator dialog = Feedback.Dialog(page, new Regex("^Remove "));
        await dialog.WaitForAsync();
        Assert.Contains("Recordings are kept", await dialog.InnerTextAsync());
        await page.Locator("[data-channel-remove-confirm]").ClickAsync();

        await wren.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 45_000 });
        await Feedback.Snackbar(page, "Channel removed").WaitForAsync();
    }

    [Fact]
    public async Task Signed_out_settings_shows_the_connect_prompt_without_channel_data()
    {
        await using Journey session = await OpenAsync(app);
        IPage page = session.Page;

        // Sign out through the top bar, then open Settings.
        await page.GetByRole(AriaRole.Button, new() { Name = "Twitch account: Mock Admin" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Disconnect" }).ClickAsync();
        await page.Locator("a[data-auth-state='disconnected']").WaitForAsync(new() { Timeout = 15_000 });
        await session.GotoAsync($"{app.BaseUrl}/settings", JourneyReady.Shell);

        ILocator prompt = page.Locator("[data-settings-signed-out]");
        await prompt.WaitForAsync();
        Assert.Contains("Connect Twitch to manage channels", await prompt.InnerTextAsync());
        Assert.Equal(0, await page.Locator("[data-channel]").CountAsync());
        Assert.Equal(
            "/api/auth/twitch/login?returnUrl=%2Fsettings",
            await prompt.Locator("a[data-twitch-connect]").GetAttributeAsync("href"));
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "settings-signed-out");
    }

    [Fact]
    public async Task An_admin_pauses_and_resumes_a_channel_and_the_library_shows_paused()
    {
        await using Journey session = await OpenAsync(app, "/settings");
        IPage page = session.Page;

        ILocator row = page.Locator("[data-channel='mock_nova']");
        await row.WaitForAsync();
        await row.Locator("[data-channel-stop]").ClickAsync();

        ILocator dialog = Feedback.Dialog(page, new Regex("^(Stop recording for|Pause) "));
        await dialog.WaitForAsync();
        Assert.Contains("stays paused", await dialog.InnerTextAsync());
        await page.Locator("[data-channel-stop-confirm]").ClickAsync();

        await row.Locator("[data-channel-resume]").WaitForAsync();
        Assert.Contains("PAUSED", await row.Locator("[data-channel-state]").InnerTextAsync());
        await Feedback.Snackbar(page, "Channel paused").WaitForAsync();
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "settings-paused");

        await session.GotoAsync(app.BaseUrl);
        await LibraryHooks.OpenLibraryAsync(page);
        await page.Locator(LibraryHooks.Channel).First.WaitForAsync();
        Assert.Contains(
            "Paused",
            await page.Locator(LibraryHooks.ChannelFor("mock_nova") + " [data-channel-meta]").TextContentAsync());
        Assert.Equal(
            0,
            await page.Locator(LibraryHooks.ChannelFor("mock_nova") + " [data-live-ring]").CountAsync());

        await session.GotoAsync($"{app.BaseUrl}/settings", JourneyReady.Shell);
        row = page.Locator("[data-channel='mock_nova']");
        await row.Locator("[data-channel-resume]").WaitForAsync();
        await row.Locator("[data-channel-resume]").ClickAsync();
        await row.Locator("[data-channel-stop]").WaitForAsync();
        await Feedback.Snackbar(page, "Recording resumed").WaitForAsync();
        Assert.Contains("WATCHING", await row.Locator("[data-channel-state]").InnerTextAsync());
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "settings-resumed");
    }

    [Fact]
    public async Task Exit_settings_returns_to_the_previous_page_or_home()
    {
        await using Journey session = await OpenAsync(app);
        IPage page = session.Page;

        // From a recording, the exit goes back to it through browser history.
        ILocator row = await LibraryHooks.RowAsync(page);
        string id = (await row.GetAttributeAsync("data-recording-id"))!;
        await session.OpenRecordingAsync(row);
        // The app's Settings controls open the sheet (#183); the account menu's deep link still
        // reaches the page.
        await page.GetByRole(AriaRole.Button, new() { Name = "Twitch account: Mock Admin" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Manage Twitch connection" }).ClickAsync();
        await Journey.WaitForUrlAsync(page, url => new Uri(url).AbsolutePath == "/settings");
        await page.Locator("[data-channel]").First.WaitForAsync();
        await page.Locator("[data-settings-exit][data-sk-ready]").WaitForAsync(
            new() { State = WaitForSelectorState.Attached });
        await page.GetByRole(AriaRole.Button, new() { Name = "Exit settings" }).ClickAsync();
        await Journey.WaitForUrlAsync(page, url => new Uri(url).AbsolutePath == $"/r/{id}");

        // Opened directly, there is no StreamKeepr page to go back to: Esc goes Home.
        await session.GotoAsync($"{app.BaseUrl}/settings", JourneyReady.Shell);
        await page.Locator("[data-channel]").First.WaitForAsync();
        await page.Locator("[data-settings-exit][data-sk-ready]").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        await page.Keyboard.PressAsync("Escape");
        await Journey.WaitForUrlAsync(page, url => new Uri(url).AbsolutePath == "/");
    }
}
