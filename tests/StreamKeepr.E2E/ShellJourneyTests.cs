using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace StreamKeepr.E2E;

[Collection(MockAppCollection.Name)]
[Trait("Category", "E2E")]
public sealed class ShellJourneyTests(MockAppFixture app)
{
    [Fact]
    public async Task App_loads_shell_renders_and_axe_is_clean()
    {
        // Branded Chrome is required because Playwright's bundled Chromium lacks H.264 (E-21).
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });

        string testName = nameof(App_loads_shell_renders_and_axe_is_clean);
        string tracePath = Path.Combine(app.ArtifactsDirectory, $"{testName}.trace.zip");
        string screenshotPath = Path.Combine(app.ArtifactsDirectory, $"{testName}.png");
        string axePath = Path.Combine(app.ArtifactsDirectory, $"{testName}.axe.json");

        BrowserNewContextOptions contextOptions = new()
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
        };

        await using IBrowserContext context = await browser.NewContextAsync(contextOptions);
        await context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true,
        });

        try
        {
            IPage page = await context.NewPageAsync();
            await page.GotoAsync(
                MockAppFixture.BaseUrl,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            Assert.Equal("Stream Keepr", await page.TitleAsync());

            ILocator shell = page.Locator(".sk-shell");
            await shell.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
            });

            Assert.True(await page.Locator("header.sk-appbar").IsVisibleAsync());
            Assert.Contains("StreamKeepr", await page.Locator("header.sk-appbar .sk-wordmark").InnerTextAsync());
            Assert.True(await page.Locator("nav#sk-drawer").IsVisibleAsync());

            ILocator main = page.Locator("main.sk-main");
            await main.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
            });
            Assert.Equal(3, await main.Locator("[data-slot]").CountAsync());

            // The shell must be styled, not just parsed: an empty static-asset body would
            // still leave the DOM present while axe color-contrast checks see no CSS.
            await page.WaitForFunctionAsync(
                "getComputedStyle(document.querySelector('h1')).position === 'absolute'");
            Assert.Equal(
                "absolute",
                await page.EvaluateAsync<string>(
                    "getComputedStyle(document.querySelector('h1')).position"));

            IReadOnlyList<BrowserContextCookiesResult> cookies = await context.CookiesAsync();
            Assert.Contains(
                cookies,
                cookie => cookie.Name == "sk-auth" && !string.IsNullOrEmpty(cookie.Value));

            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = screenshotPath,
                FullPage = true,
            });

            AxeResult results = await page.RunAxe();
            await File.WriteAllTextAsync(axePath, results.ToString());

            string violations = string.Join(
                Environment.NewLine,
                results.Violations.Select(violation =>
                    $"{violation.Id} ({violation.Impact}): {violation.HelpUrl} " +
                    $"on {string.Join(", ", violation.Nodes.Select(node => node.Target))}"));

            Assert.True(
                results.Violations.Length == 0,
                $"axe found {results.Violations.Length} violation(s):{Environment.NewLine}{violations}");
        }
        finally
        {
            await context.Tracing.StopAsync(new TracingStopOptions { Path = tracePath });
        }
    }

    [Fact]
    public async Task Drawer_renders_groups_rows_live_status_and_storage()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
        await using IBrowserContext context = await browser.NewContextAsync(
            new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
            });

        IPage page = await context.NewPageAsync();
        await page.GotoAsync(
            MockAppFixture.BaseUrl,
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await page.Locator("nav#sk-drawer .sk-group").First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        Assert.True(await page.Locator("nav#sk-drawer .sk-group").CountAsync() >= 4);
        Assert.True(await page.Locator("nav#sk-drawer a.sk-row").CountAsync() >= 7);

        await page.Locator(".sk-wordmark .rec-dot").WaitForAsync(
            new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 30_000,
            });
        await page.Locator("a.sk-row .sk-status.rec").First.WaitForAsync(
            new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 30_000,
            });

        string firstGroup = await page.Locator("nav#sk-drawer .sk-group").First.InnerTextAsync();
        Assert.Contains("Recording now", firstGroup);

        string href = await page.Locator("a.sk-row").First.GetAttributeAsync("href") ?? "";
        Assert.StartsWith("/r/", href);

        ILocator storage = page.Locator(".sk-storage");
        Assert.True(await storage.IsVisibleAsync());
        Assert.Contains("free of", await storage.GetAttributeAsync("title") ?? "");
    }

    [Fact]
    public async Task Drawer_selection_deep_link_and_reload_keep_aria_current()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
        await using IBrowserContext context = await browser.NewContextAsync(
            new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
            });

        IPage page = await context.NewPageAsync();
        await page.GotoAsync(
            MockAppFixture.BaseUrl,
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        ILocator row = page.Locator("a.sk-row", new PageLocatorOptions
        {
            HasText = "Ranked climb to Diamond",
        });
        await row.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });

        string id = await row.GetAttributeAsync("data-recording-id") ?? "";
        await row.ClickAsync();
        await page.WaitForURLAsync($"**/r/{id}", new PageWaitForURLOptions { Timeout = 30_000 });

        Assert.Equal("true", await row.GetAttributeAsync("aria-current"));
        Assert.Contains(
            "Ranked climb to Diamond",
            await page.Locator("[data-selected-recording]").InnerTextAsync());

        await page.ReloadAsync(new PageReloadOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
        });

        row = page.Locator($"a.sk-row[data-recording-id='{id}']");
        await row.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });
        Assert.Equal("true", await row.GetAttributeAsync("aria-current"));
    }

    [Fact]
    public async Task Unknown_recording_route_shows_the_message_and_link_back()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
        await using IBrowserContext context = await browser.NewContextAsync();

        IPage page = await context.NewPageAsync();
        await page.GotoAsync(
            $"{MockAppFixture.BaseUrl}/r/not-a-recording",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        ILocator unknown = page.Locator("[data-unknown-recording]");
        await unknown.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
        });

        Assert.Contains("Recording not found", await unknown.InnerTextAsync());
        Assert.Equal("/", await unknown.Locator("a").GetAttributeAsync("href"));
        Assert.Contains("Back to library", await unknown.Locator("a").InnerTextAsync());
    }

    [Fact]
    public async Task Storage_meter_updates_live_without_a_reload()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
        await using IBrowserContext context = await browser.NewContextAsync();

        IPage page = await context.NewPageAsync();
        await page.GotoAsync(
            MockAppFixture.BaseUrl,
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        ILocator storage = page.Locator(".sk-storage");
        await storage.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
        });

        string initial = await storage.InnerTextAsync();

        await Assertions.Expect(storage).Not.ToHaveTextAsync(
            initial,
            new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
    }
}
