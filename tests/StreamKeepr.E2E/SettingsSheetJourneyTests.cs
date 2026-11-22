using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

/// <summary>
/// The settings sheet (#183, design system SettingsSheet): Settings opens over the current page,
/// so a playing recording never stops. The same <c>&lt;video&gt;</c> element keeps playing while
/// the sheet opens, is used and closes, at 1280 (the end drawer) and at 375 (the full-screen
/// dialog with its now-playing strip).
/// </summary>
[Collection(MockAppCollection.Name)]
[Trait("Category", "E2E")]
public sealed class SettingsSheetJourneyTests(MockAppFixture app)
{
    private const string Ready = "[data-sk='seek'][aria-disabled='false']";

    [Fact]
    public async Task On_desktop_settings_opens_and_closes_beside_a_recording_that_keeps_playing()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        string path = await PlayFirstSavedRecordingAsync(page);

        ILocator banner = page.GetByRole(AriaRole.Banner);
        ILocator toggle = banner.GetByRole(AriaRole.Link, new() { Name = "Settings", Exact = true });
        await toggle.ClickAsync();

        // Only the query changes; the page and its player stay.
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == path && url.Contains("settings=open", StringComparison.Ordinal));
        ILocator sheet = page.GetByRole(AriaRole.Complementary, new() { Name = "Settings", Exact = true });
        await sheet.Locator("[data-channel]").First.WaitForAsync();
        await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true");
        await Assertions.Expect(sheet.GetByRole(AriaRole.Button, new() { Name = "Close settings" })).ToBeFocusedAsync();
        await AssertStillPlayingAsync(page, "with the sheet open");
        // Axe in the state the sheet opens in: focus on Close.
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "settings-sheet-1280");

        // Clicking outside never closes it: the page beside the sheet stays usable.
        // An empty spot in the app bar, between the search field and the links: inert, outside the sheet.
        await page.Mouse.ClickAsync(950, 32);
        await page.WaitForTimeoutAsync(300);
        Assert.True(await sheet.IsVisibleAsync(), "a click outside the sheet must not close it");
        Assert.Contains("settings=open", page.Url, StringComparison.Ordinal);
        await sheet.GetByRole(AriaRole.Button, new() { Name = "Close settings" }).FocusAsync();

        // Single-letter player shortcuts pause while focus is inside the sheet.
        await page.Keyboard.PressAsync("k");
        Assert.False(await VideoAsync<bool>(page, "v => v.paused"), "K inside the sheet must not pause the player");

        // Esc closes it, focus returns to the toggle, and playback carried on throughout.
        await page.Keyboard.PressAsync("Escape");
        await sheet.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await page.WaitForURLAsync(url => new Uri(url).PathAndQuery == path);
        await Assertions.Expect(toggle).ToBeFocusedAsync();
        await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
        await AssertStillPlayingAsync(page, "after Esc closed the sheet");

        // The Close button closes it, and so does Back.
        await toggle.ClickAsync();
        await sheet.Locator("[data-channel]").First.WaitForAsync();
        await sheet.GetByRole(AriaRole.Button, new() { Name = "Close settings" }).ClickAsync();
        await sheet.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await page.WaitForURLAsync(url => new Uri(url).PathAndQuery == path);
        await AssertStillPlayingAsync(page, "after Close closed the sheet");

        await toggle.ClickAsync();
        await sheet.Locator("[data-channel]").First.WaitForAsync();
        await page.GoBackAsync();
        await sheet.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await page.WaitForURLAsync(url => new Uri(url).PathAndQuery == path);
        await AssertStillPlayingAsync(page, "after Back closed the sheet");
    }

    [Fact]
    public async Task On_a_phone_the_settings_dialog_opens_over_a_playing_recording_and_its_strip_pauses_it()
    {
        await using Session session = await OpenAsync(
            app.BaseUrl, viewport: new ViewportSize { Width = 375, Height = 812 }, hasTouch: true);
        IPage page = session.Page;
        await page.Locator("[data-shell][data-layout='compact']").WaitForAsync();
        string href = (await (await LibraryHooks.RowAsync(page, LibraryHooks.SavedRow)).GetAttributeAsync("href"))!;
        await page.GotoAsync($"{app.BaseUrl}{href}");
        string path = await StartPlaybackAsync(page);

        // Watch pages have no bottom bar; the account menu's Settings opens the dialog.
        await page.GetByRole(AriaRole.Button, new() { Name = "Twitch account: Mock Admin" }).TapAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Settings", Exact = true }).TapAsync();

        ILocator dialog = Feedback.Dialog(page, "Settings");
        await dialog.Locator("[data-channel]").First.WaitForAsync();
        Assert.Equal(path, new Uri(page.Url).AbsolutePath);
        // Full screen: once MudDialog's opening animation settles, it covers the 375×812 viewport.
        await page.EvaluateAsync("() => Promise.all(document.getAnimations().filter(a => a.effect?.getComputedTiming().endTime !== Infinity).map(a => a.finished.catch(() => {})))");
        LocatorBoundingBoxResult box = (await dialog.BoundingBoxAsync())!;
        Assert.True(box.X <= 0 && box.Y <= 0 && box.Width >= 375 && box.Height >= 812, $"the dialog is {box.Width}×{box.Height} at {box.X},{box.Y}");
        await AssertStillPlayingAsync(page, "with the dialog open");
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "settings-sheet-375");

        ILocator strip = dialog.GetByRole(AriaRole.Group, new() { Name = "Now playing" });
        await Assertions.Expect(strip.Locator("[data-now-playing-status]")).ToContainTextAsync("playing");
        await strip.GetByRole(AriaRole.Button, new() { Name = "Pause" }).TapAsync();
        await page.WaitForFunctionAsync("() => window.__skVideo.paused");
        Assert.True(await VideoAsync<bool>(page, "v => v === window.__skVideo"), "the strip paused the page's own video");
        await Assertions.Expect(strip.GetByRole(AriaRole.Button, new() { Name = "Play" })).ToBeVisibleAsync();
        await Assertions.Expect(strip.Locator("[data-now-playing-status]")).ToContainTextAsync("paused");

        // And Play resumes the same video.
        await strip.GetByRole(AriaRole.Button, new() { Name = "Play" }).TapAsync();
        await page.WaitForFunctionAsync("() => !window.__skVideo.paused");
        await Assertions.Expect(strip.GetByRole(AriaRole.Button, new() { Name = "Pause" })).ToBeVisibleAsync();

        await strip.GetByRole(AriaRole.Button, new() { Name = "Back to the video" }).TapAsync();
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await page.WaitForURLAsync(url => new Uri(url).PathAndQuery == path);
        Assert.True(await VideoAsync<bool>(page, "v => v === window.__skVideo"), "the same video is still on the page");
    }

    private static async Task<string> PlayFirstSavedRecordingAsync(IPage page)
    {
        ILocator row = (await LibraryHooks.RowAsync(page, LibraryHooks.SavedRow));
        string id = (await row.GetAttributeAsync("data-recording-id"))!;
        await row.ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == $"/r/{id}");
        return await StartPlaybackAsync(page);
    }

    // Plays muted (headless autoplay policy) and marks the element, so a later check can tell
    // the same <video> from a re-created one.
    private static async Task<string> StartPlaybackAsync(IPage page)
    {
        await page.Locator(Ready).WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 20_000 });
        await page.Locator("[data-sk='video']").EvaluateAsync("v => { window.__skVideo = v; v.muted = true; return v.play(); }");
        await page.WaitForFunctionAsync("() => window.__skVideo.currentTime > 0.3");
        return new Uri(page.Url).AbsolutePath;
    }

    private static async Task AssertStillPlayingAsync(IPage page, string when)
    {
        Assert.True(await VideoAsync<bool>(page, "v => v === window.__skVideo && !v.paused"), $"the same video plays {when}");
        double before = await VideoAsync<double>(page, "v => v.currentTime");
        await page.WaitForFunctionAsync(
            "before => window.__skVideo.currentTime > before + 0.3", before, new() { Timeout = 5_000 });
    }

    private static Task<T> VideoAsync<T>(IPage page, string expression) =>
        page.Locator("[data-sk='video']").EvaluateAsync<T>(expression);
}
