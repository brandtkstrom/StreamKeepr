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

    internal static ILocator Rows(IPage page) => page.Locator("nav#sk-drawer a.sk-row");

    internal static async Task Expect(Func<Task<int>> actual, int expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        int value;
        while ((value = await actual()) != expected && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        Assert.Equal(expected, value);
    }

    internal static async Task<Session> OpenAsync(string url, ViewportSize? viewport = null)
    {
        IPlaywright playwright = await Playwright.CreateAsync();
        IBrowser browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
        IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = viewport ?? new ViewportSize { Width = 1280, Height = 820 },
        });
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
