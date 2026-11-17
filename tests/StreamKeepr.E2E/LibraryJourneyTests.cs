using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace StreamKeepr.E2E;

[Collection(MockAppCollection.Name)]
[Trait("Category", "E2E")]
public sealed class LibraryJourneyTests(MockAppFixture app)
{
    internal const string Search = "input[data-search-input]";

    [Fact]
    public async Task Search_filters_sort_and_shortcuts_live_in_the_url()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        int historyLength = await page.EvaluateAsync<int>("history.length");

        await page.Locator(Search).FillAsync("NIGHT");
        await page.WaitForURLAsync(url => url.Contains("q=NIGHT", StringComparison.Ordinal));
        await Expect(() => Rows(page).CountAsync(), 2);
        Assert.Equal(2, await page.Locator($"{LibraryHooks.Row} mark").CountAsync());
        await AssertAxeCleanAsync(page, "library-search");

        await page.Locator(Search).PressAsync("Escape");
        await page.WaitForURLAsync(url => !url.Contains("q=", StringComparison.Ordinal));
        await Rows(page).Nth(6).WaitForAsync();

        await page.Locator("main").ClickAsync();
        await page.Keyboard.PressAsync("/");
        Assert.True(await page.Locator(Search).EvaluateAsync<bool>("el => el === document.activeElement"));
        await page.Locator("main").ClickAsync();

        await page.Locator("[data-filter='saved']").ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("state=saved", StringComparison.Ordinal));
        await Expect(
            () => page.Locator($"{LibraryHooks.Row}:is([data-state='rec'], [data-state='reconnecting'])").CountAsync(),
            0);
        Assert.True(await Rows(page).CountAsync() >= 5);

        // Selecting a recording keeps the filtered view instead of resetting it.
        await Rows(page).First.ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/r/", StringComparison.Ordinal) &&
            url.Contains("state=saved", StringComparison.Ordinal));
        Assert.Equal("true", await page.Locator("[data-filter='saved']").GetAttributeAsync("aria-pressed"));
        historyLength++; // selecting a recording is real navigation; view changes are not

        await page.GetByRole(AriaRole.Button, new() { Name = "Library options" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "A–Z by streamer" }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("sort=az", StringComparison.Ordinal));
        Assert.Contains("A–Z by streamer", await page.Locator("[data-sort]").InnerTextAsync());

        // Every view change replaced the current entry instead of adding history.
        Assert.Equal(historyLength, await page.EvaluateAsync<int>("history.length"));

        await page.ReloadAsync();
        await page.Locator($"{Search}[data-sk-ready]").WaitForAsync();
        Assert.Equal("true", await page.Locator("[data-filter='saved']").GetAttributeAsync("aria-pressed"));
        Assert.Contains("A–Z by streamer", await page.Locator("[data-sort]").InnerTextAsync());

        await page.Locator(Search).FillAsync("zzzz-no-match");
        await page.Locator("[data-library-empty]").WaitForAsync();
        Assert.Equal("No recordings match", await page.Locator("[data-library-empty]").InnerTextAsync());
        Assert.Equal(0, await page.Locator("[data-streamer-group]").CountAsync());
    }

    [Fact]
    public async Task Keyboard_users_browse_the_library_as_one_tree()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator tree = page.Locator("[role='tree']");

        await page.Locator("[data-filter='saved']").FocusAsync();
        await page.Keyboard.PressAsync("Tab");
        Assert.True(await IsFocusedInsideAsync(tree));
        Assert.Equal(1, await tree.Locator("[role='treeitem'][tabindex='0']").CountAsync());

        await page.Keyboard.PressAsync("Home");
        ILocator firstHead = tree.Locator("[aria-level='1']").First;
        Assert.True(await firstHead.EvaluateAsync<bool>("el => el === document.activeElement"));

        await page.Keyboard.PressAsync("ArrowLeft");
        await Expect(async () => await firstHead.GetAttributeAsync("aria-expanded") == "false" ? 1 : 0, 1);
        await page.Keyboard.PressAsync("ArrowRight");
        await Expect(async () => await firstHead.GetAttributeAsync("aria-expanded") == "true" ? 1 : 0, 1);

        await page.Keyboard.PressAsync("ArrowDown");
        string? id = await page.EvaluateAsync<string?>("document.activeElement.dataset.recordingId");
        Assert.False(string.IsNullOrEmpty(id));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await ExpandLibraryOnWatchPageAsync(page);
        await page.Locator($"a[data-recording-id='{id}'][aria-current='true']").WaitForAsync();

        // The whole list is a single tab stop.
        await tree.Locator($"[data-recording-id='{id}']").FocusAsync();
        await page.Keyboard.PressAsync("Tab");
        Assert.False(await IsFocusedInsideAsync(tree));

        await AssertAxeCleanAsync(page, "library-tree");
    }

    [Fact]
    public async Task Rail_avatar_expands_the_drawer_at_its_group()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator drawer = page.Locator(LibraryHooks.Drawer);

        await page.Locator("[data-drawer-toggle]").ClickAsync();
        await Expect(async () => await drawer.GetAttributeAsync("data-drawer") == "rail" ? 1 : 0, 1);
        await AssertAxeCleanAsync(page, "library-rail");

        ILocator lastHead = drawer.Locator("[role='treeitem'][aria-level='1']").Last;
        string? key = await lastHead.GetAttributeAsync("data-tree-key");
        await lastHead.ClickAsync();

        await Expect(async () => await drawer.GetAttributeAsync("data-drawer") != "rail" ? 1 : 0, 1);
        await Expect(async () => await lastHead.GetAttributeAsync("aria-expanded") == "true" ? 1 : 0, 1);
        Assert.Equal(key, await drawer.Locator("[role='treeitem'][tabindex='0']").GetAttributeAsync("data-tree-key"));
    }

    [Fact]
    public async Task Clicking_a_streamer_hides_and_restores_its_recordings()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator group = page.Locator(LibraryHooks.Group).First;
        ILocator head = group.Locator("[role=treeitem][aria-level='1']");
        ILocator body = group.Locator("[role=group]");
        ILocator firstRow = body.Locator("a[data-recording-id]").First;

        Assert.Equal("true", await head.GetAttributeAsync("aria-expanded"));
        Assert.True(await firstRow.IsVisibleAsync());

        await head.ClickAsync();
        await Expect(async () => await head.GetAttributeAsync("aria-expanded") == "false" ? 1 : 0, 1);
        await Expect(
            async () => await body.EvaluateAsync<bool>("b => b.getBoundingClientRect().height < 1") ? 1 : 0,
            1);
        Assert.NotNull(await body.GetAttributeAsync("inert"));

        await head.ClickAsync();
        await Expect(async () => await head.GetAttributeAsync("aria-expanded") == "true" ? 1 : 0, 1);
        await Expect(
            async () => await body.EvaluateAsync<bool>("b => b.getBoundingClientRect().height > 0") ? 1 : 0,
            1);
        Assert.Null(await body.GetAttributeAsync("inert"));

        await firstRow.ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/r/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rail_sort_menu_opens_right_and_remains_usable()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator drawer = page.Locator(LibraryHooks.Drawer);
        ILocator trigger = page.GetByRole(AriaRole.Button, new() { Name = "Library options" });
        ILocator menu = page.Locator("[role='menu']:not([hidden])");

        // The expanded drawer keeps opening the menu leftward.
        await trigger.ClickAsync();
        await menu.WaitForAsync();
        LocatorBoundingBoxResult expandedTrigger = (await trigger.BoundingBoxAsync())!;
        LocatorBoundingBoxResult expandedMenu = (await menu.BoundingBoxAsync())!;
        Assert.True(expandedMenu.X < expandedTrigger.X, "expanded menu opens leftward");
        await page.Keyboard.PressAsync("Escape");

        await page.Locator("[data-drawer-toggle]").ClickAsync();
        await Expect(
            async () => await drawer.GetAttributeAsync("data-drawer") == "rail" ? 1 : 0, 1);

        await trigger.ClickAsync();
        await menu.WaitForAsync();

        double[] geometry = await page.EvaluateAsync<double[]>("""
            () => {
              const menu = document.querySelector("[role='menu']:not([hidden])");
              const trigger = document.querySelector("[aria-label='Library options']");
              const drawer = document.querySelector('nav[aria-label=Library]');
              if (!menu || !trigger || !drawer) return [];
              const m = menu.getBoundingClientRect();
              const t = trigger.getBoundingClientRect();
              const d = drawer.getBoundingClientRect();
              const hit = document.elementFromPoint(m.left + m.width - 8, m.top + m.height / 2);
              return [
                m.left, m.top, m.width, m.height,
                t.left, t.width,
                d.left, d.width,
                hit?.closest("[role=menu]:not([hidden])") ? 1 : 0,
              ];
            }
            """);
        Assert.Equal(9, geometry.Length);

        double menuLeft = geometry[0];
        double menuWidth = geometry[2];
        double triggerLeft = geometry[4];
        double drawerLeft = geometry[6];
        double drawerWidth = geometry[7];
        bool hit = geometry[8] == 1;

        Assert.True(menuLeft >= triggerLeft - 1, "rail menu opens rightward");
        Assert.True(
            menuLeft + menuWidth > drawerLeft + drawerWidth,
            "rail menu extends past the drawer");
        Assert.True(hit, "the rail menu is not clipped by the drawer");

        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Oldest first" }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("sort=oldest", StringComparison.Ordinal));
        await AssertAxeCleanAsync(page, "library-rail-sort-menu");
    }

    [Fact]
    public async Task A_dropped_connection_shows_the_quiet_note_not_the_modal()
    {
        // Offline emulation does not close a socket that is already open, so record the
        // circuit's sockets, go offline so reconnects fail, then drop them.
        const string recordSockets = """
            window.__skSockets = [];
            const Native = window.WebSocket;
            window.WebSocket = class extends Native {
              constructor(...args) { super(...args); window.__skSockets.push(this); }
            };
            """;
        await using Session session = await OpenAsync(app.BaseUrl, initScript: recordSockets);
        IPage page = session.Page;
        ILocator note = page.GetByRole(AriaRole.Status).Filter(new() { HasTextString = "Live updates paused" });
        Assert.False(await note.IsVisibleAsync());

        await session.Context.SetOfflineAsync(true);
        await page.EvaluateAsync("window.__skSockets.forEach(socket => socket.close())");
        await page.WaitForFunctionAsync(
            "['show','retrying'].includes(document.body.dataset.skReconnect)",
            null,
            new PageWaitForFunctionOptions { Timeout = 20_000 });

        Assert.True(await note.IsVisibleAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Dialog).CountAsync());
        await AssertAxeCleanAsync(page, "updates-paused");

        // Retry works while the circuit is down: reconnect.js serves it, not a Blazor handler.
        await page.EvaluateAsync("""
            () => {
              window.__skRetries = 0;
              const reconnect = window.Blazor.reconnect.bind(window.Blazor);
              window.Blazor.reconnect = (...args) => { window.__skRetries++; return reconnect(...args); };
            }
            """);
        await session.Context.SetOfflineAsync(false);
        await note.GetByRole(AriaRole.Button, new() { Name = "Retry" }).ClickAsync();
        Assert.True(await page.EvaluateAsync<int>("window.__skRetries") >= 1, "Retry called Blazor.reconnect");
        await page.WaitForFunctionAsync(
            "document.body.dataset.skReconnect === 'hide'",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        Assert.False(await note.IsVisibleAsync());
        await page.Locator(LibraryHooks.Group).First.WaitForAsync();
    }

    [Theory]
    [InlineData("failed", "Couldn’t rejoin the server", "Retry")]
    [InlineData("paused", "Session paused", "Resume")]
    [InlineData("resume-failed", "Couldn’t resume the session", "Resume")]
    public async Task A_hard_loss_raises_the_reconnect_dialog_with_the_cards_copy(
        string state, string title, string action)
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        await page.Locator(LibraryHooks.Group).First.WaitForAsync();

        // Raise the framework's event, so the static dialog's copy, styling and accessibility
        // are checked without losing the server.
        await page.EvaluateAsync(
            """
            state => document.getElementById('components-reconnect-modal')
              .dispatchEvent(new CustomEvent('components-reconnect-state-changed', { detail: { state } }))
            """,
            state);

        ILocator dialog = Feedback.Dialog(page, title);
        await dialog.WaitForAsync();
        Assert.True(await dialog.GetByRole(AriaRole.Button, new() { Name = action }).IsVisibleAsync());
        Assert.Equal(
            state != "paused",
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Reload" }).IsVisibleAsync());
        Assert.False(await page.GetByRole(AriaRole.Status).Filter(new() { HasTextString = "Live updates paused" }).IsVisibleAsync());
        await AssertAxeCleanAsync(page, $"reconnect-{state}");
    }

    private static Task<bool> IsFocusedInsideAsync(ILocator locator) =>
        locator.EvaluateAsync<bool>("el => el.contains(document.activeElement)");

    internal static ILocator Rows(IPage page) => page.Locator(LibraryHooks.Row);

    internal static async Task Expect(Func<Task<int>> actual, int expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        int value;
        while ((value = await actual()) != expected && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        Assert.Equal(expected, value);
    }

    /// <summary>
    /// Watch pages show the library as the rail by default (#174). Journeys that keep using the
    /// library's rows there open it with the menu, as a viewer would; the choice is remembered
    /// for later watch pages and reloads.
    /// </summary>
    internal static async Task ExpandLibraryOnWatchPageAsync(IPage page)
    {
        await page.Locator("[data-shell][data-watching]").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        ILocator drawer = page.Locator(LibraryHooks.Drawer);
        if (await drawer.GetAttributeAsync("data-drawer") == "expanded")
            return;

        await page.Locator("[data-drawer-toggle]").ClickAsync();
        await Expect(async () => await drawer.GetAttributeAsync("data-drawer") == "expanded" ? 1 : 0, 1);
    }

    internal static async Task<Session> OpenAsync(
        string url, ViewportSize? viewport = null, string? initScript = null, bool hasTouch = false)
    {
        IPlaywright playwright = await Playwright.CreateAsync();
        IBrowser browser = await playwright.Chromium.LaunchAsync(E2EBrowser.LaunchOptions);
        IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = viewport ?? new ViewportSize { Width = 1280, Height = 820 },
            HasTouch = hasTouch,
        });
        if (initScript is not null)
            await context.AddInitScriptAsync(initScript);

        IPage page = await context.NewPageAsync();
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        // Attached, not visible: on a phone the search row and the drawer start closed (#174).
        await page.Locator($"{Search}[data-sk-ready]").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        await page.Locator(LibraryHooks.Group).First.WaitForAsync(new() { State = WaitForSelectorState.Attached });
        return new Session(playwright, browser, context, page);
    }

    internal async Task AssertAxeCleanAsync(IPage page, string name, AxeRunOptions? options = null)
    {
        // Let finite entrance animations (a snackbar fading in) finish, so contrast is measured
        // on the settled page. Endless ones (spinners, the REC pulse) are left running.
        await page.EvaluateAsync(
            """
            () => Promise.all(document.getAnimations()
              .filter(a => a.effect?.getComputedTiming().endTime !== Infinity)
              .map(a => a.finished.catch(() => {})))
            """);
        AxeResult results = options is null ? await page.RunAxe() : await page.RunAxe(options);
        await File.WriteAllTextAsync(
            Path.Combine(app.ArtifactsDirectory, $"{name}.axe.json"), results.ToString());

        string violations = string.Join(
            Environment.NewLine,
            results.Violations.Select(violation =>
                $"{violation.Id} ({violation.Impact}): " +
                string.Join(", ", violation.Nodes.Select(node => node.Target))));
        Assert.True(results.Violations.Length == 0, $"{name}: {violations}");
    }

    internal sealed record Session(
        IPlaywright Playwright, IBrowser Browser, IBrowserContext Context, IPage Page)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Browser.DisposeAsync();
            Playwright.Dispose();
        }
    }
}
