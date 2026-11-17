using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

/// <summary>
/// The MudBlazor shell (#174): the phone bottom bar and temporary library drawer at 375×812
/// with touch, axe and no sideways scrolling at 1280 and 375, and the app bar's links before
/// the circuit connects.
/// </summary>
[Collection(MockAppCollection.Name)]
[Trait("Category", "E2E")]
public sealed class ShellLayoutJourneyTests(MockAppFixture app)
{
    private static readonly ViewportSize Phone = new() { Width = 375, Height = 812 };
    private static readonly ViewportSize Desktop = new() { Width = 1280, Height = 820 };

    [Fact]
    public async Task On_a_phone_the_bottom_bar_opens_settings_over_home_and_navigates_home()
    {
        await using Session session = await OpenAsync(app.BaseUrl, viewport: Phone, hasTouch: true);
        IPage page = session.Page;
        ILocator bar = page.GetByRole(AriaRole.Navigation, new() { Name = "Main" });
        await bar.WaitForAsync();

        ILocator home = bar.GetByRole(AriaRole.Link, new() { Name = "Home" });
        ILocator settings = bar.GetByRole(AriaRole.Link, new() { Name = "Settings" });
        Assert.Equal("page", await home.GetAttributeAsync("aria-current"));
        Assert.True(await page.Locator("[data-shell][data-layout='compact']").IsVisibleAsync());

        // Settings opens over the page (#183): the full-screen dialog, with Settings current.
        await settings.TapAsync();
        ILocator dialog = Feedback.Dialog(page, "Settings");
        await dialog.WaitForAsync();
        Assert.Equal("/", new Uri(page.Url).AbsolutePath);
        await Assertions.Expect(settings).ToHaveAttributeAsync("aria-current", "page");
        await Assertions.Expect(settings).ToHaveAttributeAsync("aria-expanded", "true");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Close settings" }).TapAsync();
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await Assertions.Expect(settings).ToHaveAttributeAsync("aria-expanded", "false");

        await page.GotoAsync($"{app.BaseUrl}/settings");
        await Assertions.Expect(bar.GetByRole(AriaRole.Link, new() { Name = "Settings" }))
            .ToHaveAttributeAsync("aria-current", "page");

        await bar.GetByRole(AriaRole.Link, new() { Name = "Home" }).TapAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
        await Assertions.Expect(bar.GetByRole(AriaRole.Link, new() { Name = "Home" }))
            .ToHaveAttributeAsync("aria-current", "page");
    }

    [Fact]
    public async Task On_a_phone_library_opens_the_temporary_drawer_and_picking_a_recording_closes_it()
    {
        await using Session session = await OpenAsync(app.BaseUrl, viewport: Phone, hasTouch: true);
        IPage page = session.Page;
        ILocator bar = page.GetByRole(AriaRole.Navigation, new() { Name = "Main" });
        ILocator library = bar.GetByRole(AriaRole.Button, new() { Name = "Library" });
        ILocator row = page.Locator(LibraryHooks.Row).First;

        Assert.False(await IsOnScreenAsync(row), "the drawer starts closed");
        Assert.Equal("false", await library.GetAttributeAsync("aria-expanded"));

        await library.TapAsync();
        await page.Locator("[data-shell][data-drawer-modal-open]").WaitForAsync();
        await Assertions.Expect(library).ToHaveAttributeAsync("aria-expanded", "true");
        await Assertions.Expect(library).ToHaveAttributeAsync("aria-current", "page");
        await LibraryJourneyTests.Expect(async () => await IsOnScreenAsync(row) ? 1 : 0, 1);
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "phone-library-drawer");

        string id = (await row.GetAttributeAsync("data-recording-id"))!;
        await row.TapAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await page.Locator("[data-shell]:not([data-drawer-modal-open])").WaitForAsync();

        // The watch page gives the player the room: no bottom bar, and a back arrow home.
        await bar.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        ILocator back = page.GetByRole(AriaRole.Link, new() { Name = "Back to Home" });
        await back.WaitForAsync();
        Assert.False(string.IsNullOrWhiteSpace(await page.Locator("header [data-watch-title]").InnerTextAsync()));

        await back.TapAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
        await bar.WaitForAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task On_a_phone_clear_search_leaves_focus_on_a_visible_control(bool closeSearchRowFirst)
    {
        await using Session session = await OpenAsync(app.BaseUrl, viewport: Phone, hasTouch: true);
        IPage page = session.Page;
        ILocator search = page.Locator("input[data-search-input]");
        await page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).TapAsync();
        await search.FillAsync("zzzz-no-such-stream");
        await page.WaitForURLAsync(url => url.Contains("q=", StringComparison.Ordinal));
        if (closeSearchRowFirst)
        {
            // The query stays in the URL; only the row is put away.
            await page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).TapAsync();
            await search.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        }

        ILocator bar = page.GetByRole(AriaRole.Navigation, new() { Name = "Main" });
        await bar.GetByRole(AriaRole.Button, new() { Name = "Library" }).TapAsync();
        await page.Locator("[data-shell][data-drawer-modal-open]").WaitForAsync();
        ILocator noMatch = page.Locator("[data-library-no-match]");
        await noMatch.WaitForAsync();

        await noMatch.GetByRole(AriaRole.Button, new() { Name = "Clear search" }).TapAsync();
        await page.WaitForURLAsync(url => !url.Contains("q=", StringComparison.Ordinal));
        await page.Locator("[data-shell]:not([data-drawer-modal-open])").WaitForAsync();

        // Clearing navigates, which closes the temporary drawer. With the search row open, focus
        // goes to the search field; with the row put away, the drawer hands it back to Library.
        ILocator expected = closeSearchRowFirst
            ? bar.GetByRole(AriaRole.Button, new() { Name = "Library" })
            : search;
        await LibraryJourneyTests.Expect(
            async () => await expected.EvaluateAsync<bool>("e => e === document.activeElement") ? 1 : 0,
            1);
        Assert.True(await expected.IsVisibleAsync());
        // And it stays there: nothing moves it once the drawer has closed.
        await Task.Delay(500);
        Assert.True(await expected.EvaluateAsync<bool>("e => e === document.activeElement"));
    }

    [Fact]
    public async Task Watch_pages_open_on_the_rail_and_each_page_kind_remembers_its_drawer()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator drawer = page.Locator(LibraryHooks.Drawer);
        Assert.Equal("expanded", await drawer.GetAttributeAsync("data-drawer"));
        // MudDrawer must actually be open, not just labelled expanded: the frame shows the full library.
        await LibraryJourneyTests.Expect(async () => await VisibleWidthAsync(drawer) >= 280 ? 1 : 0, 1);

        await page.Locator(LibraryHooks.Row).First.ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/r/", StringComparison.Ordinal));
        await LibraryJourneyTests.Expect(async () => await drawer.GetAttributeAsync("data-drawer") == "rail" ? 1 : 0, 1);
        await LibraryJourneyTests.Expect(async () => await VisibleWidthAsync(drawer) <= 80 ? 1 : 0, 1);

        await ExpandLibraryOnWatchPageAsync(page);
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        Assert.Equal("expanded", await page.Locator(LibraryHooks.Drawer).GetAttributeAsync("data-drawer"));

        // Collapsing on Home does not change the watch page's choice.
        await page.GetByRole(AriaRole.Link, new() { Name = "StreamKeepr home" }).ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
        await page.Locator("[data-drawer-toggle]").ClickAsync();
        await LibraryJourneyTests.Expect(async () => await drawer.GetAttributeAsync("data-drawer") == "rail" ? 1 : 0, 1);
        await page.GoBackAsync();
        await page.WaitForURLAsync(url => url.Contains("/r/", StringComparison.Ordinal));
        await LibraryJourneyTests.Expect(async () => await drawer.GetAttributeAsync("data-drawer") == "expanded" ? 1 : 0, 1);
    }

    [Theory]
    [InlineData(1280, "home")]
    [InlineData(1280, "recording")]
    [InlineData(1280, "settings")]
    [InlineData(375, "home")]
    [InlineData(375, "recording")]
    [InlineData(375, "settings")]
    public async Task The_shell_is_axe_clean_and_never_scrolls_sideways(int width, string screen)
    {
        ViewportSize viewport = width == Phone.Width ? Phone : Desktop;
        await using Session session = await OpenAsync(app.BaseUrl, viewport: viewport, hasTouch: viewport == Phone);
        IPage page = session.Page;

        switch (screen)
        {
            case "recording":
                string href = (await page.Locator(LibraryHooks.Row).First.GetAttributeAsync("href"))!;
                await page.GotoAsync($"{app.BaseUrl}{href}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                await page.Locator("[data-sk='seek'][aria-disabled='false']")
                    .WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 30_000 });
                break;
            case "settings":
                await page.GotoAsync($"{app.BaseUrl}/settings", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                await page.Locator("[data-channel]").First.WaitForAsync();
                break;
        }

        // Wait for the breakpoint to reach the circuit, so the checks see the settled layout.
        await page.Locator($"[data-shell][data-layout='{(viewport == Phone ? "compact" : "wide")}']").WaitForAsync();

        int[] overflow = await page.EvaluateAsync<int[]>("""
            () => {
              const page = document.scrollingElement;
              const main = document.querySelector('main');
              return [page.scrollWidth - page.clientWidth, main.scrollWidth - main.clientWidth];
            }
            """);
        Assert.True(overflow[0] <= 0, $"{screen} at {width}px: the page scrolls sideways by {overflow[0]}px");
        Assert.True(overflow[1] <= 0, $"{screen} at {width}px: the main column scrolls sideways by {overflow[1]}px");

        await page.ScreenshotAsync(new() { Path = Path.Combine(app.ArtifactsDirectory, $"shell-{screen}-{width}.png") });
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, $"shell-{screen}-{width}");
    }

    [Theory]
    [InlineData(1280)]
    [InlineData(375)]
    public async Task The_app_bar_and_bottom_bar_links_work_before_the_circuit_connects(int width)
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(E2EBrowser.LaunchOptions);
        await using IBrowserContext context = await browser.NewContextAsync(new()
        {
            ViewportSize = width == Phone.Width ? Phone : Desktop,
            HasTouch = width == Phone.Width,
        });
        // Without blazor.web.js the page stays the prerendered HTML: no circuit, no handlers.
        await context.RouteAsync("**/_framework/blazor.web*.js", route => route.AbortAsync());
        IPage page = await context.NewPageAsync();
        await page.GotoAsync(app.BaseUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        ILocator banner = page.GetByRole(AriaRole.Banner);
        ILocator github = banner.GetByRole(AriaRole.Link, new() { Name = "StreamKeepr on GitHub (opens in a new tab)" });
        ILocator twitch = banner.GetByRole(AriaRole.Link, new() { Name = "Twitch (opens in a new tab)" });
        foreach (ILocator link in new[] { github, twitch })
        {
            Assert.StartsWith("https://", await link.GetAttributeAsync("href"));
            Assert.Equal("_blank", await link.GetAttributeAsync("target"));
            Assert.Contains("noopener", await link.GetAttributeAsync("rel"));
        }

        Assert.Equal("/", await banner.GetByRole(AriaRole.Link, new() { Name = "StreamKeepr home" }).GetAttributeAsync("href"));
        Assert.Equal(
            "post",
            await page.Locator("form#sk-logout").GetAttributeAsync("method"));

        // Settings is a real link: from the app bar on desktop, from the bottom bar on a phone.
        ILocator settings = width == Phone.Width
            ? page.GetByRole(AriaRole.Navigation, new() { Name = "Main" }).GetByRole(AriaRole.Link, new() { Name = "Settings" })
            : banner.GetByRole(AriaRole.Link, new() { Name = "Settings" });
        await settings.ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/settings");
        await page.Locator("main h1").WaitForAsync(new() { State = WaitForSelectorState.Attached });
    }

    [Theory]
    [InlineData(1280)]
    [InlineData(375)]
    public async Task The_first_paint_already_has_the_settled_shell_geometry(int width)
    {
        // The server renders before it knows the breakpoint; CSS must still paint the shell the
        // circuit settles on, so nothing jumps when MudBreakpointProvider reports (#174).
        ViewportSize viewport = width == Phone.Width ? Phone : Desktop;
        string firstPaint = await ShellGeometryAsync(viewport, blockCircuit: true);
        string settled = await ShellGeometryAsync(viewport, blockCircuit: false);
        Assert.Equal(settled, firstPaint);
    }

    private async Task<string> ShellGeometryAsync(ViewportSize viewport, bool blockCircuit)
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(E2EBrowser.LaunchOptions);
        await using IBrowserContext context = await browser.NewContextAsync(new()
        {
            ViewportSize = viewport,
            HasTouch = viewport == Phone,
        });
        if (blockCircuit)
            await context.RouteAsync("**/_framework/blazor.web*.js", route => route.AbortAsync());
        IPage page = await context.NewPageAsync();
        // ?q= opens the phone's search row, so its position is measured too.
        await page.GotoAsync($"{app.BaseUrl}/?q=a", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        if (!blockCircuit)
            await page.Locator("[data-shell][data-breakpoint-known]").WaitForAsync(new() { State = WaitForSelectorState.Attached });

        return await page.EvaluateAsync<string>("""
            () => {
              const bar = document.querySelector('header').getBoundingClientRect();
              const frame = getComputedStyle(document.querySelector('main').parentElement);
              const search = document.querySelector('[data-search-open]');
              return `bar ${bar.height}px, main frame ${frame.paddingTop} top ${frame.paddingBottom} bottom, ` +
                `search row ${search ? search.getBoundingClientRect().top + 'px' : 'closed'}`;
            }
            """);
    }

    // The drawer's on-screen width, clipped by its MudDrawer frame.
    private static Task<int> VisibleWidthAsync(ILocator drawer) =>
        drawer.EvaluateAsync<int>("""
            e => {
              let right = e.getBoundingClientRect().right;
              for (let p = e.parentElement; p; p = p.parentElement)
                if (getComputedStyle(p).overflowX !== "visible") right = Math.min(right, p.getBoundingClientRect().right);
              return Math.round(right - e.getBoundingClientRect().left);
            }
            """);

    private static Task<bool> IsOnScreenAsync(ILocator locator) =>
        locator.EvaluateAsync<bool>("""
            e => {
              const box = e.getBoundingClientRect();
              return box.width > 0 && box.right > 0 && box.left < window.innerWidth;
            }
            """);
}
