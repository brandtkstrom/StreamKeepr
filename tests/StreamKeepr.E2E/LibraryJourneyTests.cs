using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace StreamKeepr.E2E;

[Collection(MockAppCollection.Name)]
[CaptureFailure]
[Trait("Category", "E2E")]
public sealed class LibraryJourneyTests(MockAppFixture app)
{
    internal const string Search = "input[data-search-input]";

    [Fact]
    public async Task Search_filters_sort_and_shortcuts_live_in_the_url()
    {
        await using Journey session = await OpenAsync(app);
        IPage page = session.Page;
        int historyLength = await page.EvaluateAsync<int>("history.length");
        // The library opens over the page from the menu; the app bar's search filters it live.
        await LibraryHooks.OpenLibraryAsync(page);

        await page.Locator(Search).FillAsync("NIGHT");
        await Journey.WaitForUrlAsync(page, url => url.Contains("q=NIGHT", StringComparison.Ordinal));
        // The channels count matches, not totals; each match is highlighted in its column.
        await Expect(() => TotalRecordingsAsync(page), 2);
        await (await LibraryHooks.RowAsync(page)).WaitForAsync();
        Assert.True(await page.Locator($"{LibraryHooks.Row} mark").CountAsync() >= 1);
        await AssertAxeCleanAsync(page, "library-search");

        await page.Locator(Search).PressAsync("Escape");
        await Journey.WaitForUrlAsync(page, url => !url.Contains("q=", StringComparison.Ordinal));
        await Expect(async () => await TotalRecordingsAsync(page) >= 7 ? 1 : 0, 1);

        // "/" focuses the search from anywhere outside a field.
        await page.EvaluateAsync("() => document.activeElement?.blur()");
        await page.Keyboard.PressAsync("/");
        Assert.True(await page.Locator(Search).EvaluateAsync<bool>("el => el === document.activeElement"));
        await page.EvaluateAsync("() => document.activeElement?.blur()");

        await LibraryHooks.OpenLibraryAsync(page);
        await page.Locator("[data-filter='saved']").ClickAsync();
        await Journey.WaitForUrlAsync(page, url => url.Contains("state=saved", StringComparison.Ordinal));
        await Expect(async () => (await RowStatesAsync(page)).Count(state => state is "rec" or "reconnecting"), 0);
        Assert.True(await TotalRecordingsAsync(page) >= 5);

        // Selecting a recording keeps the filtered view instead of resetting it.
        await session.OpenRecordingAsync(await LibraryHooks.RowAsync(page));
        Assert.Equal("true", await page.Locator("[data-filter='saved']").GetAttributeAsync("aria-pressed"));
        historyLength++; // selecting a recording is real navigation; view changes are not

        await page.GetByRole(AriaRole.Button, new() { Name = "Library options" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "A–Z by streamer" }).ClickAsync();
        await Journey.WaitForUrlAsync(page, url => url.Contains("sort=az", StringComparison.Ordinal));
        // NavigateTo changes the URL before the circuit renders the new label, so retry the read.
        await Assertions.Expect(page.Locator("[data-sort]")).ToContainTextAsync("A–Z by streamer");

        // Every view change replaced the current entry instead of adding history.
        Assert.Equal(historyLength, await page.EvaluateAsync<int>("history.length"));

        await session.ReloadAsync();
        await page.Locator($"{Search}[data-sk-ready]").WaitForAsync();
        await LibraryHooks.OpenLibraryAsync(page);
        Assert.Equal("true", await page.Locator("[data-filter='saved']").GetAttributeAsync("aria-pressed"));
        Assert.Contains("A–Z by streamer", await page.Locator("[data-sort]").InnerTextAsync());

        await page.Locator(Search).FillAsync("zzzz-no-match");
        await page.Locator("[data-library-empty]").WaitForAsync();
        Assert.Equal("No recordings match “zzzz-no-match”", await page.Locator("[data-library-empty]").InnerTextAsync());
        Assert.Equal(0, await page.Locator(LibraryHooks.Channel).CountAsync());
    }

    [Fact]
    public async Task Keyboard_users_move_through_the_channels_and_into_a_column()
    {
        await using Journey session = await OpenAsync(app);
        IPage page = session.Page;
        ILocator channels = page.Locator($"{LibraryHooks.Drawer} [role='listbox']");
        await LibraryHooks.OpenLibraryAsync(page);

        // The channels are one tab stop.
        await page.Locator("[data-filter='saved']").FocusAsync();
        await page.Keyboard.PressAsync("Tab");
        Assert.True(await IsFocusedInsideAsync(channels));
        Assert.Equal(1, await channels.Locator("[role='option'][tabindex='0']").CountAsync());

        await page.Keyboard.PressAsync("End");
        Assert.True(await page.Locator(LibraryHooks.Channel).Last.EvaluateAsync<bool>("el => el === document.activeElement"));
        await page.Keyboard.PressAsync("Home");
        ILocator first = page.Locator(LibraryHooks.Channel).First;
        Assert.True(await first.EvaluateAsync<bool>("el => el === document.activeElement"));
        string login = (await first.GetAttributeAsync("data-library-channel"))!;

        // → selects the channel, opens its column and moves focus to its first recording.
        await session.OpenFocusedChannelAsync();
        await Assertions.Expect(first).ToHaveAttributeAsync("aria-selected", "true");
        await page.Keyboard.PressAsync("ArrowDown");
        string? second = await page.EvaluateAsync<string?>("document.activeElement.dataset.recordingId");
        Assert.False(string.IsNullOrEmpty(second));
        await AssertAxeCleanAsync(page, "library-column");

        // ← goes back to the channel; Escape from a row does too, and leaves the drawer open.
        await page.Keyboard.PressAsync("ArrowLeft");
        Assert.True(await first.EvaluateAsync<bool>("el => el === document.activeElement"));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("() => document.activeElement?.matches('[data-recordings-column] a[data-recording-id]')");
        await page.Keyboard.PressAsync("Escape");
        Assert.True(await first.EvaluateAsync<bool>("el => el === document.activeElement"));
        await Assertions.Expect(page.Locator($"{LibraryHooks.Drawer} [data-recordings-column='{login}']")).ToBeVisibleAsync();

        // Enter on a recording opens it; Tab leaves the channels list in one step.
        await session.OpenFocusedChannelAsync();
        string? id = await page.EvaluateAsync<string?>("document.activeElement.dataset.recordingId");
        await page.Keyboard.PressAsync("Enter");
        await Journey.WaitForUrlAsync(page, url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await ExpandLibraryOnWatchPageAsync(page);
        await LibraryHooks.OpenLibraryAsync(page);
        await first.FocusAsync();
        await page.Keyboard.PressAsync("Tab");
        Assert.False(await IsFocusedInsideAsync(channels));
    }

    [Fact]
    public async Task Rail_avatar_opens_the_drawer_on_its_channel()
    {
        await using Journey session = await OpenAsync(app);
        IPage page = session.Page;
        ILocator drawer = page.Locator(LibraryHooks.Drawer);

        // Md and up the library starts as the rail, on every page.
        Assert.Equal("rail", await drawer.GetAttributeAsync("data-drawer"));
        await AssertAxeCleanAsync(page, "library-rail");

        ILocator last = page.Locator(LibraryHooks.Channel).Last;
        string login = (await last.GetAttributeAsync("data-library-channel"))!;
        // 32px avatars in the rail.
        Assert.Equal(32, (await last.Locator("[data-channel-color]").BoundingBoxAsync())!.Width, 0.5);
        await last.ClickAsync();

        await Expect(async () => await drawer.GetAttributeAsync("data-drawer") != "rail" ? 1 : 0, 1);
        await Assertions.Expect(page.Locator(LibraryHooks.ChannelFor(login))).ToHaveAttributeAsync("aria-selected", "true");
        await Assertions.Expect(page.Locator($"{LibraryHooks.Drawer} [data-recordings-column='{login}']")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_channels_recordings_open_beside_the_list_without_moving_the_page()
    {
        await using Journey session = await OpenAsync(app);
        IPage page = session.Page;
        ILocator main = page.Locator("main");
        LocatorBoundingBoxResult before = (await main.BoundingBoxAsync())!;
        // The menu opens the library over the page, so the page does not move either.
        await LibraryHooks.OpenLibraryAsync(page);
        Assert.Equal(before.X, (await main.BoundingBoxAsync())!.X, 0.5);
        await AssertAxeCleanAsync(page, "library-open");
        ILocator first = page.Locator(LibraryHooks.Channel).First;
        string login = (await first.GetAttributeAsync("data-library-channel"))!;

        await first.ClickAsync();

        ILocator column = page.Locator($"{LibraryHooks.Drawer} [data-recordings-column='{login}']");
        await Assertions.Expect(column).ToBeVisibleAsync();
        await column.EvaluateAsync("c => Promise.all(c.getAnimations().map(a => a.finished))");
        // It starts at the drawer's right edge (320px of channels) and lies over the page.
        LocatorBoundingBoxResult drawer = (await page.Locator(LibraryHooks.Drawer).BoundingBoxAsync())!;
        LocatorBoundingBoxResult box = (await column.BoundingBoxAsync())!;
        Assert.Equal(drawer.X + drawer.Width, box.X, 2.0);
        Assert.Equal(360, box.Width, 1.0);
        LocatorBoundingBoxResult after = (await main.BoundingBoxAsync())!;
        Assert.Equal(before.X, after.X, 0.5);
        Assert.Equal(before.Width, after.Width, 0.5);
        // The count on the channel matches the column's rows.
        int count = int.Parse((await first.Locator("[data-channel-count]").InnerTextAsync()).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(count, await column.Locator("a[data-recording-id]").CountAsync());

        await column.Locator("[data-column-close]").ClickAsync();
        await Assertions.Expect(column).ToBeHiddenAsync();
        await Assertions.Expect(first).ToBeFocusedAsync();

        await first.ClickAsync();
        await session.OpenRecordingAsync(await LibraryHooks.RowAsync(page));
    }

    [Fact]
    public async Task Rail_sort_menu_opens_right_and_remains_usable()
    {
        await using Journey session = await OpenAsync(app);
        IPage page = session.Page;
        ILocator drawer = page.Locator(LibraryHooks.Drawer);
        ILocator trigger = page.GetByRole(AriaRole.Button, new() { Name = "Library options" });
        ILocator menu = page.Locator("[role='menu']:not([hidden])");

        // The open library keeps opening the menu leftward.
        await LibraryHooks.OpenLibraryAsync(page);
        await trigger.ClickAsync();
        await menu.WaitForAsync();
        LocatorBoundingBoxResult expandedTrigger = (await trigger.BoundingBoxAsync())!;
        LocatorBoundingBoxResult expandedMenu = (await menu.BoundingBoxAsync())!;
        Assert.True(expandedMenu.X < expandedTrigger.X, "expanded menu opens leftward");
        // Escape closes the menu, not the library around it.
        await page.Keyboard.PressAsync("Escape");
        await menu.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        Assert.Equal("expanded", await drawer.GetAttributeAsync("data-drawer"));

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
        await Journey.WaitForUrlAsync(page, url => url.Contains("sort=oldest", StringComparison.Ordinal));
        await AssertAxeCleanAsync(page, "library-rail-sort-menu");
    }

    [Fact]
    public async Task A_dropped_connection_leaves_no_unhandled_rejection()
    {
        // Blazor's beginInvokeDotNetFromJS drops the promise of its own send, so any
        // invokeMethodAsync made while the socket is down rejects unhandled inside the framework,
        // whatever the caller catches. Record sockets and rejections, then drop the sockets.
        const string probe = """
            window.__skSockets = [];
            window.__skRejections = [];
            const Native = window.WebSocket;
            window.WebSocket = class extends Native {
              constructor(...args) { super(...args); window.__skSockets.push(this); }
            };
            window.addEventListener("unhandledrejection", (event) =>
              window.__skRejections.push(String(event.reason?.stack ?? event.reason)));
            """;
        await using Journey session = await OpenAsync(app, initScript: probe);
        IPage page = session.Page;

        await session.Context.SetOfflineAsync(true);
        await page.EvaluateAsync("window.__skSockets.forEach(socket => socket.close())");
        await page.WaitForFunctionAsync(
            "document.body.dataset.skReconnect === 'retrying'",
            null,
            new PageWaitForFunctionOptions { Timeout = 20_000 });

        // Back online, JS→.NET calls reach the circuit again: the search's debounced Search call.
        await session.Context.SetOfflineAsync(false);
        await page.EvaluateAsync("window.Blazor.reconnect()");
        await page.WaitForFunctionAsync(
            "document.body.dataset.skReconnect === 'hide'",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        await LibraryHooks.OpenLibraryAsync(page);
        await page.Locator(Search).FillAsync("NIGHT");
        await Journey.WaitForUrlAsync(page, url => url.Contains("q=NIGHT", StringComparison.Ordinal));

        string[] rejections = await page.EvaluateAsync<string[]>("window.__skRejections");
        Assert.True(rejections.Length == 0, string.Join("\n---\n", rejections));
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
        await using Journey session = await OpenAsync(app, initScript: recordSockets);
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

        // Restore the network before the retry; the click is served by reconnect.js, not a
        // Blazor handler, so it works while the circuit is down. Axe runs after recovery: a
        // long scan while offline can escalate the note to the modal before the retry (#296).
        await session.Context.SetOfflineAsync(false);
        await page.WaitForFunctionAsync(
            """
            async () => {
              try { return (await fetch('/health/ready')).ok; } catch { return false; }
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });
        await page.EvaluateAsync("""
            () => {
              window.__skRetries = 0;
              const reconnect = window.Blazor.reconnect.bind(window.Blazor);
              window.Blazor.reconnect = (...args) => { window.__skRetries++; return reconnect(...args); };
            }
            """);
        await note.GetByRole(AriaRole.Button, new() { Name = "Retry" }).ClickAsync();
        Assert.True(await page.EvaluateAsync<int>("window.__skRetries") >= 1, "Retry called Blazor.reconnect");

        // A successful reconnect sets "hide"; a failed one reloads, which clears the state
        // instead. Both leave the page healthy, so wait for the shell rather than the literal
        // "hide" value (#296).
        await session.WaitForShellAsync(timeoutMs: 30_000);
        Assert.False(await note.IsVisibleAsync());
        await page.Locator(LibraryHooks.Channel).First.WaitForAsync();

        // Axe the paused-note state after the recovery path has been verified. The modal is
        // still axe clean if the scan reaches it.
        await session.Context.SetOfflineAsync(true);
        await page.EvaluateAsync("window.__skSockets.forEach(socket => socket.close())");
        await page.WaitForFunctionAsync(
            "['show','retrying'].includes(document.body.dataset.skReconnect)",
            null,
            new PageWaitForFunctionOptions { Timeout = 20_000 });
        Assert.True(await note.IsVisibleAsync());
        await AssertAxeCleanAsync(page, "updates-paused");
    }

    [Theory]
    [InlineData("failed", "Couldn’t rejoin the server", "Retry")]
    [InlineData("paused", "Session paused", "Resume")]
    [InlineData("resume-failed", "Couldn’t resume the session", "Resume")]
    public async Task A_hard_loss_raises_the_reconnect_dialog_with_the_cards_copy(
        string state, string title, string action)
    {
        await using Journey session = await OpenAsync(app);
        IPage page = session.Page;
        await page.Locator(LibraryHooks.Channel).First.WaitForAsync();

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

    /// <summary>The recordings matching the current view: the sum of the channels' counts.</summary>
    internal static async Task<int> TotalRecordingsAsync(IPage page)
    {
        // The rail shows avatars only; the counts are in the open library.
        await LibraryHooks.OpenLibraryAsync(page);
        IReadOnlyList<string> counts = await page.Locator($"{LibraryHooks.Channel} [data-channel-count]").AllInnerTextsAsync();
        return counts.Sum(count => int.Parse(count.Trim(), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Every matching row's state, opening each channel's column in turn.</summary>
    internal static async Task<IReadOnlyList<string>> RowStatesAsync(IPage page)
    {
        var states = new List<string>();
        int channels = await page.Locator(LibraryHooks.Channel).CountAsync();
        for (int i = 0; i < channels; i++)
        {
            string login = (await page.Locator(LibraryHooks.Channel).Nth(i).GetAttributeAsync("data-library-channel"))!;
            await LibraryHooks.OpenChannelAsync(page, login);
            foreach (string? state in await page.Locator(LibraryHooks.Row).EvaluateAllAsync<string?[]>("rows => rows.map(r => r.dataset.state)"))
                states.Add(state ?? "");
        }

        return states;
    }

    internal static async Task Expect(Func<Task<int>> actual, int expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        int value;
        while ((value = await actual()) != expected && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        Assert.Equal(expected, value);
    }

    /// <summary>
    /// Waits for the watch page. The library there is the rail; it opens over the page (and the
    /// player) only when a journey uses it, through <see cref="LibraryHooks.OpenLibraryAsync"/>,
    /// so it never covers the player's controls by accident.
    /// </summary>
    internal static Task ExpandLibraryOnWatchPageAsync(IPage page) =>
        page.Locator("[data-shell][data-watching]").WaitForAsync(new() { State = WaitForSelectorState.Attached });

    [Fact]
    public async Task A_search_with_no_match_offers_to_clear_it()
    {
        await using Journey session = await OpenAsync(app);
        IPage page = session.Page;
        await LibraryHooks.OpenLibraryAsync(page);

        await page.Locator(Search).FillAsync("zzzz-no-such-stream");
        ILocator noMatch = page.Locator("[data-library-no-match]");
        await noMatch.WaitForAsync();
        Assert.Contains("No recordings match “zzzz-no-such-stream”", await noMatch.InnerTextAsync());
        await AssertAxeCleanAsync(page, "library-no-match");

        await noMatch.GetByRole(AriaRole.Button, new() { Name = "Clear search" }).ClickAsync();
        await Journey.WaitForUrlAsync(page, url => !url.Contains("q=", StringComparison.Ordinal));
        await Expect(async () => await TotalRecordingsAsync(page) >= 7 ? 1 : 0, 1);
        Assert.Equal("", await page.Locator(Search).InputValueAsync());
        await Expect(
            async () => await page.Locator(Search).EvaluateAsync<bool>("el => el === document.activeElement") ? 1 : 0,
            1);
    }

    internal static async Task<Journey> OpenAsync(
        MockAppFixture app, string? path = null, ViewportSize? viewport = null,
        string? initScript = null, bool hasTouch = false)
    {
        string url = path is null ? app.BaseUrl : new Uri(new Uri(app.BaseUrl), path).ToString();
        Journey journey = await Journey.OpenAsync(app, url, viewport, initScript, hasTouch);
        try
        {
            // The seeded journeys assume channels exist. The empty-library and private-mode
            // classes open their own sessions, where that assumption does not hold.
            await journey.Page.Locator(LibraryHooks.Channel).First.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
            return journey;
        }
        catch
        {
            await journey.DisposeAsync();
            throw;
        }
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
        // Only a failing check leaves its axe output behind.
        if (results.Violations.Length > 0)
        {
            await File.WriteAllTextAsync(
                Path.Combine(app.ArtifactsDirectory, $"{name}.axe.json"), results.ToString());
        }

        string violations = string.Join(
            Environment.NewLine,
            results.Violations.Select(violation =>
                $"{violation.Id} ({violation.Impact}): " +
                string.Join(", ", violation.Nodes.Select(node => node.Target))));
        Assert.True(results.Violations.Length == 0, $"{name}: {violations}");
    }
}
