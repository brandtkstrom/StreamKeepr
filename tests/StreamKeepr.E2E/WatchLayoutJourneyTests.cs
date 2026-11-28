using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

/// <summary>
/// The watch layout (#175, #332): the player-first desktop sizing, the docked chat drawer and
/// its reserved width, the full-width theater view, and the phone's sticky edge-to-edge player
/// with Chat, Details and Stats tabs.
/// </summary>
[Collection(MockAppCollection.Name)]
[CaptureFailure]
[Trait("Category", "E2E")]
public sealed class WatchLayoutJourneyTests(MockAppFixture app)
{
    private static readonly ViewportSize Phone = new() { Width = 375, Height = 812 };
    private static readonly ViewportSize Desktop = new() { Width = 1280, Height = 820 };
    private const string Ready = "[data-sk='seek'][aria-disabled='false']";

    [Fact]
    public async Task On_a_phone_the_player_stays_visible_while_the_tabs_scroll_and_switch()
    {
        await using Journey session = await OpenAsync(app, viewport: Phone, hasTouch: true);
        IPage page = session.Page;
        // A finished recording with no chapters: the assertion below is about the tab list,
        // not about whichever recording the live feed sorted first. Not "Chill art stream": the
        // Mock feed reopens mock_echo's latest session live from 30 s to 60 s after start
        // (MockLiveFeedOptions), so whether that row exists depends on when this test runs.
        await OpenRecordingAsync(page, "Late night jazz");
        await SettleAsync(page, compact: true);

        ILocator player = page.Locator("[data-sk-player]");
        LocatorBoundingBoxResult playerBox = (await player.BoundingBoxAsync())!;
        Assert.True(Math.Abs(playerBox.X) <= 1, $"the player starts at {playerBox.X}px");
        Assert.True(Math.Abs(playerBox.Width - Phone.Width) <= 1,
            $"the player is {playerBox.Width}px wide in a {Phone.Width}px view");
        Assert.Equal("0px", await player.EvaluateAsync<string>(
            "el => getComputedStyle(el).borderTopLeftRadius"));

        ILocator chatTab = page.GetByRole(AriaRole.Tab, new() { Name = "Chat" });
        ILocator chaptersTab = page.GetByRole(AriaRole.Tab, new() { Name = "Chapters" });
        ILocator statsTab = page.GetByRole(AriaRole.Tab, new() { Name = "Stats" });
        await chatTab.WaitForAsync();
        Assert.Equal("true", await chatTab.GetAttributeAsync("aria-selected"));
        // This recording has no chapters, so it has no Chapters tab; the export row and actions
        // stay with the title block, outside the tabs.
        Assert.Equal(0, await chaptersTab.CountAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Tab, new() { Name = "Details" }).CountAsync());
        await Assertions.Expect(statsTab).ToBeVisibleAsync();
        Assert.True(await page.Locator("[role='tabpanel'] [data-sk-chat]").IsVisibleAsync());
        // The tab form has no header and no Close control; the tab owns the disclosure (#333).
        Assert.Equal(0,
            await page.Locator("[role='tabpanel'] [data-sk-chat] > header").CountAsync());
        Assert.Equal(0,
            await page.GetByRole(AriaRole.Button, new() { Name = "Close chat" }).CountAsync());

        // Selecting a tab shows its panel and hides the others.
        await ScrollTopAsync(page);
        await statsTab.ClickAsync();
        await Assertions.Expect(statsTab).ToHaveAttributeAsync("aria-selected", "true");
        ILocator stats = page.Locator("[role='tabpanel'] [data-sk-stats]");
        await Assertions.Expect(stats).ToBeVisibleAsync();
        Assert.Equal("true", await stats.GetAttributeAsync("data-open"));
        Assert.False(await page.Locator("[role='tabpanel'] [data-sk-chat]").IsVisibleAsync());

        await ScrollTopAsync(page);
        await chatTab.ClickAsync();
        await Assertions.Expect(page.Locator("[role='tabpanel'] [data-sk-chat]")).ToBeVisibleAsync();

        // Scroll the main column to its end; the sticky player keeps its place under the bar.
        await page.Locator("main").EvaluateAsync("el => { el.scrollTop = el.scrollHeight; }");
        await page.WaitForFunctionAsync("() => document.querySelector('main').scrollTop > 100");
        LocatorBoundingBoxResult stuck = (await player.BoundingBoxAsync())!;
        double appBar = await page.EvaluateAsync<double>(
            "() => parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--appbar-height-dense'))");
        Assert.InRange((double)stuck.Y, appBar - 1, appBar + 1);
        Assert.True(stuck.Y + stuck.Height <= Phone.Height,
            $"the player leaves the viewport: {stuck.Y} + {stuck.Height}");

        await AssertNoSidewaysScrollAsync(page, "watch-phone");
        await page.ScreenshotAsync(new() { Path = Path.Combine(app.ArtifactsDirectory, "watch-phone.png") });
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "watch-phone");

        // A recording with chapters adds the Chapters tab before Stats, holding the list.
        await session.GotoAsync(app.BaseUrl);
        await OpenRecordingAsync(page, "Chat picks the route");
        await SettleAsync(page, compact: true);
        ILocator chapters = page.GetByRole(AriaRole.Tab, new() { Name = "Chapters" });
        await chapters.ClickAsync();
        await Assertions.Expect(chapters).ToHaveAttributeAsync("aria-selected", "true");
        await Assertions.Expect(page.Locator("[role='tabpanel'] [data-chapter-list]")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task At_a_short_desktop_the_player_is_height_capped_and_keeps_the_title_visible()
    {
        var shortDesktop = new ViewportSize { Width = 1280, Height = 600 };
        await using Journey session = await OpenAsync(app, viewport: shortDesktop);
        IPage page = session.Page;
        await OpenFirstRecordingAsync(page);
        await SettleAsync(page, compact: false);

        LocatorBoundingBoxResult player = (await page.Locator("[data-sk-player]").BoundingBoxAsync())!;
        double cap = await page.EvaluateAsync<double>("""
            () => {
              const style = getComputedStyle(document.documentElement);
              const px = (name) => parseFloat(style.getPropertyValue(name));
              return window.innerHeight - px('--appbar-height')
                - px('--space-4') - px('--space-6') - px('--player-peek');
            }
            """);
        Assert.InRange((double)player.Height, cap - 1, cap + 1);
        Assert.True(player.Width * 9 / 16 > player.Height + 20,
            $"the capped frame letterboxes: {player.Width}×{player.Height}");

        LocatorBoundingBoxResult title = (await page.Locator("[data-details-title]").BoundingBoxAsync())!;
        Assert.True(title.Y + title.Height <= shortDesktop.Height,
            $"the title ends at {title.Y + title.Height}px in a {shortDesktop.Height}px view");
    }

    [Fact]
    public async Task On_a_phone_in_landscape_the_player_fills_the_screen()
    {
        var landscape = new ViewportSize { Width = 812, Height = 375 };
        await using Journey session = await OpenAsync(app, viewport: landscape, hasTouch: true);
        IPage page = session.Page;
        await OpenFirstRecordingAsync(page);
        await SettleAsync(page, compact: true);

        LocatorBoundingBoxResult player = (await page.Locator("[data-sk-player]").BoundingBoxAsync())!;
        Assert.True(Math.Abs(player.X) <= 1, $"the player starts at {player.X}px");
        Assert.True(Math.Abs(player.Y) <= 1, $"the player starts at {player.Y}px");
        Assert.True(Math.Abs(player.Width - landscape.Width) <= 1,
            $"the player is {player.Width}px wide in an {landscape.Width}px view");
        Assert.True(Math.Abs(player.Height - landscape.Height) <= 1,
            $"the player is {player.Height}px tall in a {landscape.Height}px view");

        await AssertNoSidewaysScrollAsync(page, "watch-landscape");
        await page.ScreenshotAsync(new() { Path = Path.Combine(app.ArtifactsDirectory, "watch-landscape.png") });
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "watch-landscape");
    }

    [Fact]
    public async Task At_1280_theater_mode_keeps_the_open_chat_drawer_docked_at_the_player_divider()
    {
        await using Journey session = await OpenAsync(app, viewport: Desktop);
        IPage page = session.Page;
        await OpenFirstRecordingAsync(page);
        await SettleAsync(page, compact: false);

        ILocator player = page.Locator("[data-sk-player]");
        LocatorBoundingBoxResult before = (await player.BoundingBoxAsync())!;
        Assert.True(before.Width < Desktop.Width, "the shell's drawer and gutter inset the player");

        await page.Locator("[data-sk='theater']").ClickAsync();
        await page.Locator("[data-shell][data-theater-mode]").WaitForAsync();
        await page.WaitForFunctionAsync(
            $"() => document.querySelector('[data-sk-player]').getBoundingClientRect().width >= {Desktop.Width - 1}");

        LocatorBoundingBoxResult full = (await player.BoundingBoxAsync())!;
        Assert.True(Math.Abs(full.X) <= 1, $"the player starts at {full.X}px");
        Assert.True(Math.Abs(full.Width - Desktop.Width) <= 1,
            $"the player is {full.Width}px wide in a {Desktop.Width}px view");
        Assert.Equal("0px", await player.EvaluateAsync<string>(
            "el => getComputedStyle(el).borderTopLeftRadius"));

        // Open the drawer: the same geometry as the normal watch view, and the player reaches its
        // divider with no gutter.
        await OpenChatAsync(page);
        LocatorBoundingBoxResult drawerBox = await AssertDockedDrawerAsync(page);
        ILocator drawer = page.Locator("[data-watch-chat]");
        ILocator playerSlot = page.Locator("[data-slot='player']");
        LocatorBoundingBoxResult atDivider = (await playerSlot.BoundingBoxAsync())!;
        Assert.True(Math.Abs(atDivider.X + atDivider.Width - drawerBox.X) <= 1,
            $"the player ends at {atDivider.X + atDivider.Width}px, the divider at {drawerBox.X}px");

        // The content below keeps its 16px side padding instead of reaching the divider.
        ILocator details = page.Locator("[data-slot='details']");
        ILocator stats = page.Locator("[data-slot='viewers']");
        Assert.Equal("16px", await details.EvaluateAsync<string>("el => getComputedStyle(el).paddingRight"));
        Assert.Equal("16px", await stats.EvaluateAsync<string>("el => getComputedStyle(el).paddingRight"));
        LocatorBoundingBoxResult playerBox = (await player.BoundingBoxAsync())!;
        LocatorBoundingBoxResult detailsBox = (await details.BoundingBoxAsync())!;
        Assert.True(detailsBox.Y >= playerBox.Y + playerBox.Height - 1, "details sit below the player");

        // The open drawer is the page under test: no sideways scroll, and axe clean.
        await AssertNoSidewaysScrollAsync(page, "watch-theater");
        await page.ScreenshotAsync(new() { Path = Path.Combine(app.ArtifactsDirectory, "watch-theater.png") });
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "watch-theater");

        // Closing removes it from layout again: the player takes the whole width.
        await page.Locator("[data-sk-player] [data-sk='chat']").ClickAsync();
        await page.WaitForFunctionAsync(
            "() => document.querySelector('[data-sk-chat]').dataset.open === 'false'");
        Assert.False(await drawer.IsVisibleAsync());
        await page.WaitForFunctionAsync(
            $"() => document.querySelector('[data-sk-player]').getBoundingClientRect().width >= {Desktop.Width - 1}");
    }

    [Fact]
    public async Task At_1280_the_open_chat_is_a_docked_drawer_and_the_content_reserves_its_width()
    {
        await using Journey session = await OpenAsync(app, viewport: Desktop);
        IPage page = session.Page;
        await OpenFirstRecordingAsync(page);
        await SettleAsync(page, compact: false);

        await OpenChatAsync(page);
        ILocator pane = page.Locator("[data-sk-chat]");
        ILocator drawer = page.Locator("[data-watch-chat]");
        ILocator toggle = page.Locator("[data-sk-player] [data-sk='chat']");
        double chatWidth = await page.EvaluateAsync<double>(
            "() => parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--chat-width'))");
        Assert.InRange(chatWidth, 399d, 401d);
        LocatorBoundingBoxResult drawerBox = await AssertDockedDrawerAsync(page);

        // Flat drawer surface: one left divider, square corners, no shadow or inset.
        string[] surface = await pane.EvaluateAsync<string[]>(
            """
            el => {
              const s = getComputedStyle(el);
              return [s.borderTopWidth, s.borderRightWidth, s.borderBottomWidth, s.borderLeftWidth,
                      s.borderLeftStyle, s.borderTopLeftRadius, s.boxShadow, s.margin];
            }
            """);
        Assert.Equal(new[] { "0px", "0px", "0px", "1px", "solid", "0px", "none", "0px" }, surface);

        // The player and title block stop 16px left of the drawer; the scroll container reserves it.
        LocatorBoundingBoxResult player = (await page.Locator("[data-slot='player']").BoundingBoxAsync())!;
        LocatorBoundingBoxResult details = (await page.Locator("[data-slot='details']").BoundingBoxAsync())!;
        Assert.True(Math.Abs(player.X + player.Width - (drawerBox.X - 16)) <= 1,
            $"the player ends at {player.X + player.Width}px, the drawer starts at {drawerBox.X}px");
        Assert.True(Math.Abs(details.X + details.Width - (drawerBox.X - 16)) <= 1,
            $"the title block ends at {details.X + details.Width}px, the drawer starts at {drawerBox.X}px");
        LocatorBoundingBoxResult main = (await page.Locator("main").BoundingBoxAsync())!;
        Assert.True(Math.Abs(main.X + main.Width - drawerBox.X) <= 1,
            $"the main column ends at {main.X + main.Width}px, the drawer starts at {drawerBox.X}px");

        // It stays docked while the content scrolls.
        await page.Locator("main").EvaluateAsync("el => { el.scrollTop = el.scrollHeight; }");
        LocatorBoundingBoxResult scrolled = (await drawer.BoundingBoxAsync())!;
        Assert.True(Math.Abs(scrolled.Y - drawerBox.Y) <= 1, $"the drawer moved from {drawerBox.Y}px to {scrolled.Y}px");
        await page.Locator("main").EvaluateAsync("el => { el.scrollTop = 0; }");

        // The open drawer is the page under test: no sideways scroll, and axe clean.
        await AssertNoSidewaysScrollAsync(page, "watch-desktop-chat");
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "watch-desktop-chat");

        // Closed, it takes no width and the player expands to the full column.
        await toggle.ClickAsync();
        await page.WaitForFunctionAsync(
            "() => document.querySelector('[data-sk-chat]').dataset.open === 'false'");
        Assert.False(await drawer.IsVisibleAsync());
        await page.WaitForFunctionAsync(
            $"() => document.querySelector('main').getBoundingClientRect().right >= {Desktop.Width - 1}");
        LocatorBoundingBoxResult closedMain = (await page.Locator("main").BoundingBoxAsync())!;
        Assert.True(Math.Abs(closedMain.X + closedMain.Width - Desktop.Width) <= 1,
            $"the closed drawer still reserves room: main ends at {closedMain.X + closedMain.Width}px");
        await page.WaitForFunctionAsync(
            $"() => document.querySelector('[data-slot=player]').getBoundingClientRect().right >= {Desktop.Width - 16 - 1}");
        LocatorBoundingBoxResult expanded = (await page.Locator("[data-slot='player']").BoundingBoxAsync())!;
        Assert.True(Math.Abs(expanded.X + expanded.Width - (Desktop.Width - 16)) <= 1,
            $"the player ends at {expanded.X + expanded.Width}px, the view gutter at {Desktop.Width - 16}px");
    }

    [Fact]
    public async Task On_a_phone_a_persisted_open_chat_never_paints_the_desktop_drawer()
    {
        await using Journey session = await OpenAsync(app, viewport: Desktop);
        IPage page = session.Page;
        await OpenFirstRecordingAsync(page);
        await SettleAsync(page, compact: false);

        // Persist the open state through the player's own toggle, then keep the server's wide
        // prerender: without the circuit the phone's first paint still has the desktop pane in
        // the DOM, and CSS must hide it.
        await OpenChatAsync(page);
        await page.WaitForFunctionAsync(
            "() => decodeURIComponent(document.cookie).includes('\"chat\":true')");

        await page.SetViewportSizeAsync(Phone.Width, Phone.Height);
        await SettleAsync(page, compact: true);
        // Journey.ReloadAsync waits for the shell's interactive marker; this reload deliberately
        // has no circuit so the static first paint can be inspected (testing contract §6.1).
        await page.RouteAsync("**/_framework/blazor.web*.js", route => route.AbortAsync());
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        ILocator desktopDrawer = page.Locator("[data-watch-chat]");
        await desktopDrawer.WaitForAsync(new() { State = WaitForSelectorState.Attached });
        Assert.Equal("true", await page.Locator("[data-sk-chat]").GetAttributeAsync("data-open"));
        Assert.False(await desktopDrawer.IsVisibleAsync(), "the desktop drawer must not paint on a phone");
        Assert.False(await page.Locator("[data-sk-chat]").IsVisibleAsync());
    }

    [Fact]
    public async Task Crossing_the_breakpoint_keeps_the_same_video_element()
    {
        await using Journey session = await OpenAsync(app, viewport: Desktop);
        IPage page = session.Page;
        await OpenFirstRecordingAsync(page);
        await SettleAsync(page, compact: false);

        await page.Locator("[data-sk='video']").EvaluateAsync("v => { v.__skMarker = true; }");
        await page.SetViewportSizeAsync(Phone.Width, Phone.Height);
        await SettleAsync(page, compact: true);

        Assert.True(await page.Locator("[data-sk='video']").EvaluateAsync<bool>("v => v.__skMarker === true"));
        await page.GetByRole(AriaRole.Tab, new() { Name = "Stats" }).WaitForAsync();
    }

    /// <summary>Opens the desktop chat drawer when it is closed.</summary>
    private static async Task OpenChatAsync(IPage page)
    {
        if (await page.Locator("[data-sk-chat]").GetAttributeAsync("data-open") == "true")
            return;

        await page.Locator("[data-sk-player] [data-sk='chat']").ClickAsync();
        await page.WaitForFunctionAsync(
            "() => document.querySelector('[data-sk-chat]').dataset.open === 'true'");
    }

    /// <summary>
    /// Asserts the docked drawer's geometry at 1280 and returns its box: flush right, from the
    /// app bar's bottom to the viewport's bottom, and chat-width wide.
    /// </summary>
    private static async Task<LocatorBoundingBoxResult> AssertDockedDrawerAsync(IPage page)
    {
        await page.WaitForFunctionAsync(
            "() => document.querySelector('[data-watch-chat]')?.getBoundingClientRect().width > 0");
        LocatorBoundingBoxResult box = (await page.Locator("[data-watch-chat]").BoundingBoxAsync())!;
        double chatWidth = await page.EvaluateAsync<double>(
            "() => parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--chat-width'))");
        double headerBottom = await page.EvaluateAsync<double>(
            "() => document.querySelector('header').getBoundingClientRect().bottom");

        Assert.True(Math.Abs(box.Width - chatWidth) <= 1,
            $"the drawer is {box.Width}px wide, the token {chatWidth}px");
        Assert.True(Math.Abs(box.X + box.Width - Desktop.Width) <= 1,
            $"the drawer ends at {box.X + box.Width}px in a {Desktop.Width}px view");
        Assert.True(Math.Abs(box.Y - headerBottom) <= 1,
            $"the drawer starts at {box.Y}px, the app bar ends at {headerBottom}px");
        Assert.True(Math.Abs(box.Y + box.Height - Desktop.Height) <= 1,
            $"the drawer ends at {box.Y + box.Height}px, the view at {Desktop.Height}px");
        return box;
    }

    [Fact]
    public async Task A_long_title_steps_down_two_type_sizes_and_a_short_one_returns_to_the_base()
    {
        await using Journey session = await OpenAsync(app, viewport: Desktop);
        IPage page = session.Page;
        await OpenRecordingAsync(page, "Late night jazz");
        await SettleAsync(page, compact: false);

        // The component measures on first render. No seeded recording has a title long enough
        // to exercise both steps, so this test calls the same module with changing text; the
        // component's invocation is covered by RecordingDetailsTests.
        const string mediumDesktop =
            "A medium-length recording title that fills more than one line at the desktop width, wraps onto a second line, and still stops well before the third line";
        Assert.Equal("1", await SetTitleAsync(page, mediumDesktop));
        Assert.Equal("20px", await TitleFontSizeAsync(page));

        string longTitle = string.Concat(
            Enumerable.Repeat("A very long recording title ", 30));
        Assert.Equal("2", await SetTitleAsync(page, longTitle));
        Assert.Equal("18px", await TitleFontSizeAsync(page));

        Assert.Equal("none", await SetTitleAsync(page, "Short"));
        Assert.Equal("24px", await TitleFontSizeAsync(page));

        // Compact repeats the two steps at their smaller sizes: 18px and 16px over a 20px base.
        await page.SetViewportSizeAsync(Phone.Width, Phone.Height);
        await SettleAsync(page, compact: true);

        const string mediumCompact = "A medium-length title that wraps once";
        Assert.Equal("1", await SetTitleAsync(page, mediumCompact));
        Assert.Equal("18px", await TitleFontSizeAsync(page));

        Assert.Equal("2", await SetTitleAsync(page, longTitle));
        Assert.Equal("16px", await TitleFontSizeAsync(page));

        Assert.Equal("none", await SetTitleAsync(page, "Short"));
        Assert.Equal("20px", await TitleFontSizeAsync(page));
    }

    private static async Task<string> SetTitleAsync(IPage page, string text) =>
        await page.EvaluateAsync<string>("""
            async (value) => {
              const module = await import('/Components/Features/Details/RecordingDetails.razor.js');
              const title = document.querySelector('[data-details-title]');
              const summary = document.querySelector('[data-details-summary]');
              title.textContent = value;
              module.watchTitle(summary);
              return title.getAttribute('data-title-step') ?? 'none';
            }
            """, text);

    private static async Task<string> TitleFontSizeAsync(IPage page) =>
        await page.EvaluateAsync<string>(
            "getComputedStyle(document.querySelector('[data-details-title]')).fontSize");

    private static async Task OpenRecordingAsync(IPage page, string title)
    {
        string href = (await (await LibraryHooks.RowAsync(page, LibraryHooks.Row, title)).First
            .GetAttributeAsync("href"))!;
        await page.GotoAsync(new Uri(new Uri(page.Url), href).ToString(),
            new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 30_000 });
    }

    private static async Task OpenFirstRecordingAsync(IPage page)
    {
        string href = (await (await LibraryHooks.RowAsync(page, LibraryHooks.SavedRow))
            .GetAttributeAsync("href"))!;
        await page.GotoAsync(new Uri(new Uri(page.Url), href).ToString(),
            new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 30_000 });
    }

    private static async Task SettleAsync(IPage page, bool compact)
    {
        await page.Locator($"[data-shell][data-layout='{(compact ? "compact" : "wide")}']").WaitForAsync();
        if (compact)
            await page.Locator("[role='tab']").First.WaitForAsync();
    }

    private static async Task ScrollTopAsync(IPage page)
    {
        await page.Locator("main").EvaluateAsync("el => { el.scrollTop = 0; }");
        await page.WaitForFunctionAsync("() => document.querySelector('main').scrollTop === 0");
    }

    private static async Task AssertNoSidewaysScrollAsync(IPage page, string screen)
    {
        int[] overflow = await page.EvaluateAsync<int[]>("""
            () => {
              const page = document.scrollingElement;
              const main = document.querySelector('main');
              return [page.scrollWidth - page.clientWidth, main.scrollWidth - main.clientWidth];
            }
            """);
        Assert.True(overflow[0] <= 0, $"{screen}: the page scrolls sideways by {overflow[0]}px");
        Assert.True(overflow[1] <= 0, $"{screen}: the main column scrolls sideways by {overflow[1]}px");
    }
}
