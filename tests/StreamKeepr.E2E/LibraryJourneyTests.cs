using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace StreamKeepr.E2E;

[Collection(MockAppCollection.Name)]
[Trait("Category", "E2E")]
public sealed class LibraryJourneyTests(MockAppFixture app)
{
    private const string Search = "input[data-search-input]";

    [Fact]
    public async Task Search_filters_sort_and_shortcuts_live_in_the_url()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        int historyLength = await page.EvaluateAsync<int>("history.length");

        await page.Locator(Search).FillAsync("NIGHT");
        await page.WaitForURLAsync(url => url.Contains("q=NIGHT", StringComparison.Ordinal));
        await Expect(() => Rows(page).CountAsync(), 2);
        Assert.Equal(2, await page.Locator("a.sk-row mark.sk-hl").CountAsync());
        await AssertAxeCleanAsync(page, "library-search");

        await page.Locator(Search).PressAsync("Escape");
        await page.WaitForURLAsync(url => !url.Contains("q=", StringComparison.Ordinal));
        await Rows(page).Nth(6).WaitForAsync();

        await page.Locator("main").ClickAsync();
        await page.Keyboard.PressAsync("/");
        Assert.True(await page.Locator(Search).EvaluateAsync<bool>("el => el === document.activeElement"));
        await page.Locator("main").ClickAsync();

        await page.Locator(".sk-filters [data-filter='saved']").ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("state=saved", StringComparison.Ordinal));
        await Expect(
            () => page.Locator("a.sk-row[data-state='rec'], a.sk-row[data-state='reconnecting']").CountAsync(),
            0);
        Assert.True(await Rows(page).CountAsync() >= 5);

        // Selecting a recording keeps the filtered view instead of resetting it.
        await Rows(page).First.ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/r/", StringComparison.Ordinal) &&
            url.Contains("state=saved", StringComparison.Ordinal));
        Assert.Equal("true", await page.Locator(".sk-filters [data-filter='saved']").GetAttributeAsync("aria-pressed"));
        historyLength++; // selecting a recording is real navigation; view changes are not

        await page.GetByRole(AriaRole.Button, new() { Name = "Library options" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "A–Z by streamer" }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("sort=az", StringComparison.Ordinal));
        Assert.Contains("A–Z by streamer", await page.Locator(".sk-sort").InnerTextAsync());

        // Every view change replaced the current entry instead of adding history.
        Assert.Equal(historyLength, await page.EvaluateAsync<int>("history.length"));

        await page.ReloadAsync();
        await page.Locator($"{Search}[data-sk-ready]").WaitForAsync();
        Assert.Equal("true", await page.Locator(".sk-filters [data-filter='saved']").GetAttributeAsync("aria-pressed"));
        Assert.Contains("A–Z by streamer", await page.Locator(".sk-sort").InnerTextAsync());

        await page.Locator(Search).FillAsync("zzzz-no-match");
        await page.Locator("[data-library-empty]").WaitForAsync();
        Assert.Equal("No recordings match", await page.Locator("[data-library-empty]").InnerTextAsync());
        Assert.Equal(0, await page.Locator(".sk-group").CountAsync());
    }

    [Fact]
    public async Task Keyboard_users_browse_the_library_as_one_tree()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator tree = page.Locator("[role='tree']");

        await page.Locator(".sk-filters [data-filter='saved']").FocusAsync();
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
        await page.Locator($"a.sk-row[data-recording-id='{id}'][aria-current='true']").WaitForAsync();

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
        ILocator drawer = page.Locator("nav#sk-drawer");

        await page.Locator("[data-drawer-toggle]").ClickAsync();
        await Expect(async () => await drawer.GetAttributeAsync("class") is { } c && c.Contains("rail") ? 1 : 0, 1);
        await AssertAxeCleanAsync(page, "library-rail");

        ILocator lastHead = drawer.Locator("[role='treeitem'][aria-level='1']").Last;
        string? key = await lastHead.GetAttributeAsync("data-tree-key");
        await lastHead.ClickAsync();

        await Expect(async () => await drawer.GetAttributeAsync("class") is { } c && !c.Contains("rail") ? 1 : 0, 1);
        await Expect(async () => await lastHead.GetAttributeAsync("aria-expanded") == "true" ? 1 : 0, 1);
        Assert.Equal(key, await drawer.Locator("[role='treeitem'][tabindex='0']").GetAttributeAsync("data-tree-key"));
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
        ILocator note = page.Locator("[data-updates-paused]");
        Assert.False(await note.IsVisibleAsync());

        await session.Context.SetOfflineAsync(true);
        await page.EvaluateAsync("window.__skSockets.forEach(socket => socket.close())");
        await page.WaitForFunctionAsync(
            "['show','retrying'].includes(document.body.dataset.skReconnect)",
            null,
            new PageWaitForFunctionOptions { Timeout = 20_000 });

        Assert.True(await note.IsVisibleAsync());
        Assert.False(await page.Locator("#components-reconnect-modal").EvaluateAsync<bool>("d => d.open"));
        await AssertAxeCleanAsync(page, "updates-paused");

        await session.Context.SetOfflineAsync(false);
        await page.WaitForFunctionAsync(
            "document.body.dataset.skReconnect === 'hide'",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        Assert.False(await note.IsVisibleAsync());
        await page.Locator("nav#sk-drawer .sk-group").First.WaitForAsync();
    }

    private static Task<bool> IsFocusedInsideAsync(ILocator locator) =>
        locator.EvaluateAsync<bool>("el => el.contains(document.activeElement)");

    internal static ILocator Rows(IPage page) => page.Locator("nav#sk-drawer a.sk-row");

    internal static async Task Expect(Func<Task<int>> actual, int expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        int value;
        while ((value = await actual()) != expected && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        Assert.Equal(expected, value);
    }

    internal static async Task<Session> OpenAsync(
        string url, ViewportSize? viewport = null, string? initScript = null)
    {
        IPlaywright playwright = await Playwright.CreateAsync();
        IBrowser browser = await playwright.Chromium.LaunchAsync(E2EBrowser.LaunchOptions);
        IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = viewport ?? new ViewportSize { Width = 1280, Height = 820 },
        });
        if (initScript is not null)
            await context.AddInitScriptAsync(initScript);

        IPage page = await context.NewPageAsync();
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator($"{Search}[data-sk-ready]").WaitForAsync();
        await page.Locator("nav#sk-drawer .sk-group").First.WaitForAsync();
        return new Session(playwright, browser, context, page);
    }

    internal async Task AssertAxeCleanAsync(IPage page, string name)
    {
        AxeResult results = await page.RunAxe();
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
