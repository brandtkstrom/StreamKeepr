using Microsoft.Playwright;

namespace StreamKeepr.E2E;

/// <summary>
/// The first-run empty states (#185) against a Mock app with seeding off: no channels and no
/// recordings, which the shared fixture's eight recordings can never show.
/// </summary>
[Trait("Category", "E2E")]
public sealed class EmptyLibraryJourneyTests : IAsyncLifetime
{
    private const string Search = "input[data-search-input]";

    // Its own app, so the shared fixture's journeys keep their recordings and their sign-in.
    private readonly MockAppFixture _app = new(
        "empty-library",
        new Dictionary<string, string>
        {
            ["Mock__Seed__Enabled"] = "false",
            ["Mock__Feed__Enabled"] = "false",
        });

    public Task InitializeAsync() => _app.InitializeAsync();

    public Task DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task The_empty_library_leads_to_channels_or_to_Twitch_and_is_axe_clean()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(E2EBrowser.LaunchOptions);
        await using IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 820 },
        });
        IPage page = await context.NewPageAsync();
        var axe = new LibraryJourneyTests(_app);

        // Signed in (the Mock admin): one way forward, to Settings at Channels.
        await OpenAsync(page, _app.BaseUrl);
        ILocator empty = page.Locator("[data-library-empty-home]");
        await empty.WaitForAsync();
        Assert.Equal("No recordings yet", await empty.GetByRole(AriaRole.Heading).InnerTextAsync());
        await axe.AssertAxeCleanAsync(page, "empty-library-signed-in");

        // A short window puts Channels below the fold, so reaching it takes the #channels scroll.
        await page.SetViewportSizeAsync(1280, 400);
        await empty.GetByRole(AriaRole.Link, new() { Name = "Add a channel" }).ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith("/settings#channels", StringComparison.Ordinal));
        ILocator channels = page.Locator("section#channels");
        await channels.WaitForAsync();
        // The main column is the scroller; the section starts in view only because it scrolled.
        await LibraryJourneyTests.Expect(
            async () => await channels.EvaluateAsync<bool>("""
                e => {
                  const r = e.getBoundingClientRect();
                  return e.closest('main').scrollTop > 0 && r.top >= 0 && r.top < window.innerHeight;
                }
                """) ? 1 : 0,
            1);
        await page.SetViewportSizeAsync(1280, 820);

        // A filter with no match: "Show all recordings" clears it and hands focus to All.
        await OpenAsync(page, $"{_app.BaseUrl}/?state=recording");
        await LibraryHooks.OpenLibraryAsync(page);
        ILocator noMatch = page.Locator("[data-library-no-match]");
        await noMatch.WaitForAsync();
        Assert.Contains("Nothing is recording right now.", await noMatch.InnerTextAsync());
        await axe.AssertAxeCleanAsync(page, "library-no-match-filter");
        await noMatch.GetByRole(AriaRole.Button, new() { Name = "Show all recordings" }).ClickAsync();
        await page.WaitForURLAsync(url => !url.Contains("state=", StringComparison.Ordinal));
        ILocator all = page.Locator("[data-filter='all']");
        await LibraryJourneyTests.Expect(
            async () => await all.EvaluateAsync<bool>("e => e === document.activeElement") ? 1 : 0,
            1);
        Assert.Equal("true", await all.GetAttributeAsync("aria-pressed"));

        // Signed out, the one action is Connect with Twitch, because channels need Twitch.
        await OpenAsync(page, $"{_app.BaseUrl}/settings");
        await page.GetByRole(AriaRole.Button, new() { Name = "Disconnect" }).ClickAsync();
        await page.Locator("[data-settings-signed-out]").WaitForAsync(new() { Timeout = 15_000 });
        await OpenAsync(page, _app.BaseUrl);
        await empty.WaitForAsync();
        ILocator connect = empty.GetByRole(AriaRole.Link, new() { Name = "Connect with Twitch" });
        Assert.Equal("/api/auth/twitch/login?returnUrl=%2F", await connect.GetAttributeAsync("href"));
        Assert.Equal(0, await empty.GetByRole(AriaRole.Link, new() { Name = "Add a channel" }).CountAsync());
        await axe.AssertAxeCleanAsync(page, "empty-library-signed-out");
    }

    private static async Task OpenAsync(IPage page, string url)
    {
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator($"{Search}[data-sk-ready]").WaitForAsync(new() { State = WaitForSelectorState.Attached });
    }
}
