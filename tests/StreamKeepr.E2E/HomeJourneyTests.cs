using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

/// <summary>
/// Home's rows (#182): Recording now and Recent recordings, the phone's swipe rows, and
/// "Show all". Read-only against the shared Mock app.
/// </summary>
[Collection(MockAppCollection.Name)]
[Trait("Category", "E2E")]
public sealed class HomeJourneyTests(MockAppFixture app)
{
    private static readonly ViewportSize Phone = new() { Width = 375, Height = 812 };

    [Fact]
    public async Task At_375_the_rows_swipe_without_scrolling_the_page_and_are_axe_clean()
    {
        await using Session session = await OpenAsync(app.BaseUrl, viewport: Phone, hasTouch: true);
        IPage page = session.Page;
        await page.Locator("[data-shell][data-layout='compact']").WaitForAsync();
        ILocator recent = HomeRows.Recent(page);
        await HomeRows.Cards(recent).First.WaitForAsync();

        // The phone sizes: active cards at 85% and recent cards at 60% of the screen width.
        Assert.InRange(await CardWidthAsync(HomeRows.Now(page)) / Phone.Width, 0.84, 0.86);
        Assert.InRange(await CardWidthAsync(recent) / Phone.Width, 0.59, 0.61);
        Assert.False(await page.Locator("[data-row-scroll]").First.IsVisibleAsync(), "chevrons hide below Md");

        // A touch swipe across the row moves the row, not the page.
        LocatorBoundingBoxResult box = (await recent.BoundingBoxAsync())!;
        ICDPSession cdp = await page.Context.NewCDPSessionAsync(page);
        await cdp.SendAsync("Input.synthesizeScrollGesture", new Dictionary<string, object>
        {
            ["x"] = box.X + (box.Width * 0.8),
            ["y"] = box.Y + (box.Height / 2),
            ["xDistance"] = -240,
            ["yDistance"] = 0,
            ["gestureSourceType"] = "touch",
            ["speed"] = 1200,
        });
        await Expect(async () => await recent.EvaluateAsync<int>("row => row.scrollLeft") > 0 ? 1 : 0, 1);
        int[] overflow = await page.EvaluateAsync<int[]>("""
            () => {
              const page = document.scrollingElement;
              const main = document.querySelector('main');
              return [page.scrollWidth - page.clientWidth, window.scrollX, main.scrollWidth - main.clientWidth];
            }
            """);
        Assert.True(overflow[0] <= 0, $"the page scrolls sideways by {overflow[0]}px");
        Assert.Equal(0, overflow[1]);
        Assert.True(overflow[2] <= 0, $"the main column scrolls sideways by {overflow[2]}px");

        await page.ScreenshotAsync(new() { Path = Path.Combine(app.ArtifactsDirectory, "home-rows-375.png") });
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "home-rows-375");
    }

    private static async Task<double> CardWidthAsync(ILocator row) =>
        (await row.GetByRole(AriaRole.Listitem).First.BoundingBoxAsync())!.Width;

    [Fact]
    public async Task Cards_are_links_to_the_recording_and_show_all_opens_the_saved_library()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator recent = HomeRows.Recent(page);
        ILocator card = HomeRows.Cards(recent).First;
        await card.WaitForAsync();
        Assert.Equal(
            $"/r/{await card.GetAttributeAsync("data-recording-id")}",
            await card.GetAttributeAsync("href"));
        Assert.True(await HomeRows.Cards(HomeRows.Now(page)).CountAsync() >= 1, "the growing capture is recording");

        // The mouse-only chevron pages the row by its visible width.
        Assert.Equal(0, await recent.EvaluateAsync<int>("row => row.scrollLeft"));
        await page.Locator("[data-row-scroll='1']").ClickAsync();
        await Expect(async () => await recent.EvaluateAsync<int>("row => row.scrollLeft") > 0 ? 1 : 0, 1);
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "home-rows-1280");

        await page.GetByRole(AriaRole.Button, new() { Name = "Show all" }).ClickAsync();

        await page.WaitForURLAsync(url => new Uri(url).Query == "?state=saved");
        await Assertions.Expect(page.Locator("[data-filter='saved']")).ToHaveAttributeAsync("aria-pressed", "true");
        Assert.Equal("expanded", await page.Locator(LibraryHooks.Drawer).GetAttributeAsync("data-drawer"));
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "home-rows-1280");
    }

    [Fact]
    public async Task On_a_phone_show_all_opens_the_temporary_drawer_on_the_saved_filter()
    {
        await using Session session = await OpenAsync(app.BaseUrl, viewport: Phone, hasTouch: true);
        IPage page = session.Page;
        await page.Locator("[data-shell][data-layout='compact']").WaitForAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Show all" }).TapAsync();

        await page.WaitForURLAsync(url => new Uri(url).Query == "?state=saved");
        await page.Locator("[data-shell][data-drawer-modal-open]").WaitForAsync();
        await Assertions.Expect(page.Locator("[data-filter='saved']")).ToHaveAttributeAsync("aria-pressed", "true");
    }
}

/// <summary>
/// An open session completing moves its card between the rows. A second tab stops the
/// growing capture from its details card, as an admin would, while the first tab stays on
/// Home. Stopping is destructive, so this class owns a disposable app, with the timed feed off
/// so nothing else changes the rows.
/// </summary>
[Trait("Category", "E2E")]
public sealed class HomeRowMoveJourneyTests : IAsyncLifetime
{
    private const string GrowingTitle = "Community day";

    private readonly MockAppFixture _app = new("home-row-move", new Dictionary<string, string>
    {
        ["Mock__Feed__Enabled"] = "false",
    });

    public Task InitializeAsync() => _app.InitializeAsync();

    public Task DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task An_open_session_moves_from_the_first_row_to_the_second_when_it_completes()
    {
        await using Session session = await OpenAsync(_app.BaseUrl);
        IPage home = session.Page;
        ILocator now = HomeRows.Now(home);
        ILocator recent = HomeRows.Recent(home);

        ILocator open = HomeRows.Cards(now).Filter(new() { HasTextString = GrowingTitle });
        await open.WaitForAsync();
        string id = (await open.GetAttributeAsync("data-recording-id"))!;
        Assert.DoesNotContain(id, await HomeRows.IdsAsync(recent));

        // A marker that a reload would wipe.
        await home.EvaluateAsync("() => { window.skHomeMarker = true; }");

        // Another tab completes the session.
        IPage admin = await session.Context.NewPageAsync();
        await admin.GotoAsync($"{_app.BaseUrl}/r/{id}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        // Click only once the circuit is interactive; a click on the prerendered button is lost.
        await admin.Locator($"{Search}[data-sk-ready]").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        await admin.Locator("button[data-stop-recording]").ClickAsync();
        await admin.Locator("[data-stop-confirm]").ClickAsync();
        await Feedback.Snackbar(admin, "Channel paused").WaitForAsync();
        await admin.CloseAsync();

        // Home was never reloaded: the card moved to the second row.
        ILocator moved = recent.Locator($"a[data-recording-id='{id}']");
        await moved.WaitForAsync(new() { Timeout = 15_000 });
        Assert.DoesNotContain(id, await HomeRows.IdsAsync(now));
        Assert.Contains(await moved.GetAttributeAsync("data-state"), new[] { "saved", "partial" });
        Assert.True(await home.EvaluateAsync<bool>("() => window.skHomeMarker === true"), "Home was reloaded");
    }
}

/// <summary>Home's rows, found by their list role and heading name.</summary>
internal static class HomeRows
{
    public static ILocator Now(IPage page) => page.GetByRole(AriaRole.List, new() { Name = "Recording now", Exact = true });

    public static ILocator Recent(IPage page) => page.GetByRole(AriaRole.List, new() { Name = "Recent recordings", Exact = true });

    public static ILocator Cards(ILocator row) => row.Locator("a[data-recording-id]");

    public static async Task<string[]> IdsAsync(ILocator row) =>
        [.. (await Cards(row).EvaluateAllAsync<string[]>("cards => cards.map(card => card.dataset.recordingId)"))];
}
