using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

/// <summary>
/// The MudBlazor shell (#174): the phone bottom bar and temporary library drawer at 375×812
/// with touch, axe and no sideways scrolling at 1280 and 375, and the app bar's links before
/// the circuit connects.
/// </summary>
[Collection(MockAppCollection.Name)]
[CaptureFailure]
[Trait("Category", "E2E")]
public sealed class ShellLayoutJourneyTests(MockAppFixture app)
{
    private static readonly ViewportSize Phone = new() { Width = 375, Height = 812 };
    private static readonly ViewportSize Desktop = new() { Width = 1280, Height = 820 };

    [Fact]
    public async Task On_a_phone_the_bottom_bar_opens_settings_over_home_and_navigates_home()
    {
        await using Journey session = await OpenAsync(app, viewport: Phone, hasTouch: true);
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

        await session.GotoAsync($"{app.BaseUrl}/settings", JourneyReady.Shell);
        await Assertions.Expect(bar.GetByRole(AriaRole.Link, new() { Name = "Settings" }))
            .ToHaveAttributeAsync("aria-current", "page");

        await bar.GetByRole(AriaRole.Link, new() { Name = "Home" }).TapAsync();
        await Journey.WaitForUrlAsync(page, url => new Uri(url).AbsolutePath == "/");
        await Assertions.Expect(bar.GetByRole(AriaRole.Link, new() { Name = "Home" }))
            .ToHaveAttributeAsync("aria-current", "page");
    }

    [Fact]
    public async Task On_a_phone_library_opens_the_temporary_drawer_and_picking_a_recording_closes_it()
    {
        await using Journey session = await OpenAsync(app, viewport: Phone, hasTouch: true);
        IPage page = session.Page;
        ILocator bar = page.GetByRole(AriaRole.Navigation, new() { Name = "Main" });
        ILocator library = bar.GetByRole(AriaRole.Button, new() { Name = "Library" });
        ILocator channel = page.Locator(LibraryHooks.Channel).First;

        Assert.False(await IsOnScreenAsync(channel), "the drawer starts closed");
        Assert.Equal("false", await library.GetAttributeAsync("aria-expanded"));

        await library.TapAsync();
        await page.Locator("[data-shell][data-drawer-modal-open]").WaitForAsync();
        await Assertions.Expect(library).ToHaveAttributeAsync("aria-expanded", "true");
        await Assertions.Expect(library).ToHaveAttributeAsync("aria-current", "page");
        await LibraryJourneyTests.Expect(async () => await IsOnScreenAsync(channel) ? 1 : 0, 1);
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "phone-library-drawer");

        // There is no room for two panes: a channel's recordings drill in over the index, with
        // a way back. The toolbar stays above it, and every row shows its Delete button.
        string login = (await channel.GetAttributeAsync("data-library-channel"))!;
        await channel.TapAsync();
        ILocator column = page.Locator($"{LibraryHooks.Drawer} [data-recordings-column='{login}']");
        await Assertions.Expect(column.GetByRole(AriaRole.Button, new() { Name = "All channels" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("input[data-search-input]")).ToBeVisibleAsync();
        await column.EvaluateAsync("c => Promise.all(c.getAnimations().map(a => a.finished))");
        LocatorBoundingBoxResult drawerBox = (await page.Locator(LibraryHooks.Drawer).BoundingBoxAsync())!;
        LocatorBoundingBoxResult columnBox = (await column.BoundingBoxAsync())!;
        Assert.Equal(drawerBox.X, columnBox.X, 1.0);
        Assert.True(columnBox.Width <= drawerBox.Width + 1, "the pane drills in inside the drawer");
        Assert.Equal("1", await column.Locator("button[data-row-delete]").First.EvaluateAsync<string>("b => getComputedStyle(b).opacity"));
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "phone-library-drill-in");
        ILocator row = column.Locator("a[data-recording-id]").First;

        string id = (await row.GetAttributeAsync("data-recording-id"))!;
        await session.ActAsync(() => row.TapAsync());
        await Journey.WaitForUrlAsync(page, url => new Uri(url).AbsolutePath == $"/r/{id}");
        await session.WaitForRecordingAsync();
        await page.Locator("[data-shell]:not([data-drawer-modal-open])").WaitForAsync();

        // The watch page gives the player the room: no bottom bar, and a back arrow home.
        await bar.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        ILocator back = page.GetByRole(AriaRole.Link, new() { Name = "Back to Home" });
        await back.WaitForAsync();
        Assert.False(string.IsNullOrWhiteSpace(await page.Locator("header [data-watch-title]").InnerTextAsync()));

        await session.ActAsync(() => back.TapAsync());
        await Journey.WaitForUrlAsync(page, url => new Uri(url).AbsolutePath == "/");
        await session.WaitForShellAsync();
        await bar.WaitForAsync();
    }

    [Fact]
    public async Task On_a_phone_clear_search_keeps_the_drawer_open_with_focus_in_the_field()
    {
        await using Journey session = await OpenAsync(app, viewport: Phone, hasTouch: true);
        IPage page = session.Page;
        await LibraryHooks.OpenLibraryAsync(page);
        await page.Locator("[data-shell][data-drawer-modal-open]").WaitForAsync();
        ILocator search = page.Locator("input[data-search-input]");
        await search.FillAsync("zzzz-no-such-stream");
        await Journey.WaitForUrlAsync(page, url => url.Contains("q=", StringComparison.Ordinal));

        ILocator noMatch = page.Locator("[data-library-no-match]");
        await noMatch.WaitForAsync();
        await noMatch.GetByRole(AriaRole.Button, new() { Name = "Clear search" }).TapAsync();
        await Journey.WaitForUrlAsync(page, url => !url.Contains("q=", StringComparison.Ordinal));

        // Clearing is a query change, not a page change: the temporary drawer stays open and
        // focus stays where the viewer was typing.
        await page.Locator("[data-shell][data-drawer-modal-open]").WaitForAsync();
        await LibraryJourneyTests.Expect(
            async () => await search.EvaluateAsync<bool>("e => e === document.activeElement") ? 1 : 0,
            1);
        Assert.True(await search.IsVisibleAsync());
        await Task.Delay(500);
        Assert.True(await search.EvaluateAsync<bool>("e => e === document.activeElement"));
    }

    [Fact]
    public async Task The_library_opens_over_the_rail_without_moving_the_page_and_closes_after_a_pick()
    {
        await using Journey session = await OpenAsync(app);
        IPage page = session.Page;
        ILocator drawer = page.Locator(LibraryHooks.Drawer);
        ILocator main = page.Locator("main");

        // Every page starts on the rail, Home included, and the page sits beside it.
        await page.Locator("[data-shell][data-breakpoint-known]").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        Assert.Equal("rail", await drawer.GetAttributeAsync("data-drawer"));
        await LibraryJourneyTests.Expect(async () => await VisibleWidthAsync(drawer) <= 80 ? 1 : 0, 1);
        double left = (await main.BoundingBoxAsync())!.X;

        // The menu opens the full library over the page: MudDrawer is really open (the frame
        // shows the full library), and the page does not move.
        await page.Locator("[data-drawer-toggle]").ClickAsync();
        await LibraryJourneyTests.Expect(async () => await drawer.GetAttributeAsync("data-drawer") == "expanded" ? 1 : 0, 1);
        await LibraryJourneyTests.Expect(async () => await VisibleWidthAsync(drawer) >= 280 ? 1 : 0, 1);
        Assert.Equal(left, (await main.BoundingBoxAsync())!.X, 0.5);

        // A press outside it closes it.
        await page.Mouse.ClickAsync(900, 600);
        await LibraryJourneyTests.Expect(async () => await drawer.GetAttributeAsync("data-drawer") == "rail" ? 1 : 0, 1);

        // Escape closes it too.
        await page.Locator("[data-drawer-toggle]").ClickAsync();
        await LibraryJourneyTests.Expect(async () => await drawer.GetAttributeAsync("data-drawer") == "expanded" ? 1 : 0, 1);
        await page.Locator(LibraryHooks.Channel).First.FocusAsync();
        await page.Keyboard.PressAsync("Escape");
        await LibraryJourneyTests.Expect(async () => await drawer.GetAttributeAsync("data-drawer") == "rail" ? 1 : 0, 1);

        // Picking a saved recording closes it, and the watch page is on the rail. A live row can
        // be ending while the mock feed rotates, and its player has no seek bar to wait for.
        await session.OpenRecordingAsync(await LibraryHooks.RowAsync(page, LibraryHooks.SavedRow));
        await LibraryJourneyTests.Expect(async () => await drawer.GetAttributeAsync("data-drawer") == "rail" ? 1 : 0, 1);
        await LibraryJourneyTests.Expect(async () => await VisibleWidthAsync(drawer) <= 80 ? 1 : 0, 1);
    }

    [Fact]
    public async Task An_open_library_is_not_remembered_across_a_reload()
    {
        await using Journey session = await OpenAsync(app);
        IPage page = session.Page;
        await LibraryHooks.OpenLibraryAsync(page);

        await session.ReloadAsync();
        await page.Locator("[data-shell][data-breakpoint-known]").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        ILocator drawer = page.Locator(LibraryHooks.Drawer);

        // A full load starts MudDrawer closed, at the rail's width. Breakpoint.Always used to
        // force it open on the first viewport report, painting the library at 320px.
        Assert.Equal("rail", await drawer.GetAttributeAsync("data-drawer"));
        await LibraryJourneyTests.Expect(async () => await VisibleWidthAsync(drawer) <= 80 ? 1 : 0, 1);
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
        await using Journey session = await OpenAsync(app, viewport: viewport, hasTouch: viewport == Phone);
        IPage page = session.Page;

        switch (screen)
        {
            case "recording":
                string href = (await (await LibraryHooks.RowAsync(page)).GetAttributeAsync("href"))!;
                await session.GotoAsync($"{app.BaseUrl}{href}", JourneyReady.Shell);
                await page.Locator("[data-sk='seek'][aria-disabled='false']")
                    .WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 30_000 });
                break;
            case "settings":
                await session.GotoAsync($"{app.BaseUrl}/settings", JourneyReady.Shell);
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
        await using Journey session = await Journey.OpenAsync(
            app, url: null, viewport: width == Phone.Width ? Phone : Desktop,
            hasTouch: width == Phone.Width, ready: JourneyReady.None);
        IPage page = session.Page;
        // Without blazor.web.js the page stays the prerendered HTML: no circuit, no handlers.
        await page.RouteAsync("**/_framework/blazor.web*.js", route => route.AbortAsync());
        await page.GotoAsync(app.BaseUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        ILocator banner = page.GetByRole(AriaRole.Banner);
        // Outbound links are reference, not shell destinations: they live in Settings › About.
        Assert.Equal(0, await banner.GetByRole(AriaRole.Link, new() { Name = "StreamKeepr on GitHub (opens in a new tab)" }).CountAsync());
        Assert.Equal(0, await banner.GetByRole(AriaRole.Link, new() { Name = "Twitch (opens in a new tab)" }).CountAsync());

        Assert.Equal("/", await banner.GetByRole(AriaRole.Link, new() { Name = "StreamKeepr home" }).GetAttributeAsync("href"));
        Assert.Equal(
            "post",
            await page.Locator("form#sk-logout").GetAttributeAsync("method"));

        // Settings is a real link: from the app bar on desktop, from the bottom bar on a phone.
        ILocator settings = width == Phone.Width
            ? page.GetByRole(AriaRole.Navigation, new() { Name = "Main" }).GetByRole(AriaRole.Link, new() { Name = "Settings" })
            : banner.GetByRole(AriaRole.Link, new() { Name = "Settings" });
        await settings.ClickAsync();
        await Journey.WaitForUrlAsync(page, url => new Uri(url).AbsolutePath == "/settings");
        await page.Locator("main h1").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        foreach (var name in new[] { "StreamKeepr on GitHub (opens in a new tab)", "Twitch (opens in a new tab)" })
        {
            ILocator link = page.GetByRole(AriaRole.Link, new() { Name = name });
            await link.WaitForAsync();
            Assert.StartsWith("https://", await link.GetAttributeAsync("href"));
            Assert.Equal("_blank", await link.GetAttributeAsync("target"));
            Assert.Contains("noopener", await link.GetAttributeAsync("rel"));
        }
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
        await using Journey session = await Journey.OpenAsync(
            app, url: null, viewport: viewport, hasTouch: viewport == Phone,
            ready: JourneyReady.None);
        IPage page = session.Page;
        if (blockCircuit)
            await page.RouteAsync("**/_framework/blazor.web*.js", route => route.AbortAsync());
        if (blockCircuit)
            await page.GotoAsync($"{app.BaseUrl}/?q=a", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        else
            await session.GotoAsync($"{app.BaseUrl}/?q=a", JourneyReady.Shell);
        if (!blockCircuit)
            await page.Locator("[data-shell][data-breakpoint-known]").WaitForAsync(new() { State = WaitForSelectorState.Attached });

        return await page.EvaluateAsync<string>("""
            () => {
              const bar = document.querySelector('header').getBoundingClientRect();
              const frame = getComputedStyle(document.querySelector('main').parentElement);
              return `bar ${bar.height}px, main frame ${frame.paddingTop} top ${frame.paddingBottom} bottom`;
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
