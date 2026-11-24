using Microsoft.Playwright;

namespace StreamKeepr.E2E;

/// <summary>
/// The first-run empty states (#185) against a Mock app with seeding off: no channels and no
/// recordings, which the shared fixture's eight recordings can never show.
/// </summary>
[CaptureFailure]
[Trait("Category", "E2E")]
public sealed class EmptyLibraryJourneyTests : IAsyncLifetime
{
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
        await using Journey session = await Journey.OpenAsync(
            _app, _app.BaseUrl, ready: JourneyReady.Shell);
        IPage page = session.Page;
        var axe = new LibraryJourneyTests(_app);

        // Signed in (the Mock admin): one way forward, to Settings at Channels.
        ILocator empty = page.Locator("[data-library-empty-home]");
        await empty.WaitForAsync();
        Assert.Equal("No recordings yet", await empty.GetByRole(AriaRole.Heading).InnerTextAsync());
        await axe.AssertAxeCleanAsync(page, "empty-library-signed-in");

        // A short window puts Channels below the fold, so reaching it takes the #channels scroll.
        await page.SetViewportSizeAsync(1280, 400);
        await empty.GetByRole(AriaRole.Link, new() { Name = "Add a channel" }).ClickAsync();
        await Journey.WaitForUrlAsync(page, url => url.EndsWith("/settings#channels", StringComparison.Ordinal));
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
        await session.GotoAsync($"{_app.BaseUrl}/?state=recording", JourneyReady.Shell);
        await LibraryHooks.OpenLibraryAsync(page);
        ILocator noMatch = page.Locator("[data-library-no-match]");
        await noMatch.WaitForAsync();
        Assert.Contains("Nothing is recording right now.", await noMatch.InnerTextAsync());
        await axe.AssertAxeCleanAsync(page, "library-no-match-filter");
        await noMatch.GetByRole(AriaRole.Button, new() { Name = "Show all recordings" }).ClickAsync();
        await Journey.WaitForUrlAsync(page, url => !url.Contains("state=", StringComparison.Ordinal));
        ILocator all = page.Locator("[data-filter='all']");
        await LibraryJourneyTests.Expect(
            async () => await all.EvaluateAsync<bool>("e => e === document.activeElement") ? 1 : 0,
            1);
        Assert.Equal("true", await all.GetAttributeAsync("aria-pressed"));

        // Signed out, the one action is Connect with Twitch, because channels need Twitch.
        await session.GotoAsync($"{_app.BaseUrl}/settings", JourneyReady.Shell);
        await page.GetByRole(AriaRole.Button, new() { Name = "Disconnect" }).ClickAsync();
        await page.Locator("[data-settings-signed-out]").WaitForAsync(new() { Timeout = 15_000 });
        await session.GotoAsync(_app.BaseUrl, JourneyReady.Shell);
        await empty.WaitForAsync();
        ILocator connect = empty.GetByRole(AriaRole.Link, new() { Name = "Connect with Twitch" });
        Assert.Equal("/api/auth/twitch/login?returnUrl=%2F", await connect.GetAttributeAsync("href"));
        Assert.Equal(0, await empty.GetByRole(AriaRole.Link, new() { Name = "Add a channel" }).CountAsync());
        await axe.AssertAxeCleanAsync(page, "empty-library-signed-out");
    }

}
