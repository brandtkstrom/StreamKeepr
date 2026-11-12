using System.Globalization;
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
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(E2EBrowser.LaunchOptions);

        string testName = nameof(App_loads_shell_renders_and_axe_is_clean);
        string tracePath = Path.Combine(app.ArtifactsDirectory, $"{testName}.trace.zip");
        string screenshotPath = Path.Combine(app.ArtifactsDirectory, $"{testName}.png");

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
                app.BaseUrl,
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

            await AssertAxeCleanAsync(page, testName);
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
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(E2EBrowser.LaunchOptions);
        await using IBrowserContext context = await browser.NewContextAsync(
            new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
            });

        IPage page = await context.NewPageAsync();
        await page.GotoAsync(
            app.BaseUrl,
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

        await Assertions.Expect(page.Locator("nav#sk-drawer .sk-group").First)
            .ToContainTextAsync(
                "Recording now",
                new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });

        string href = await page.Locator("a.sk-row").First.GetAttributeAsync("href") ?? "";
        Assert.StartsWith("/r/", href);

        ILocator storage = page.Locator(".sk-storage");
        Assert.True(await storage.IsVisibleAsync());
        Assert.Contains("free of", await storage.GetAttributeAsync("title") ?? "");

        await AssertAxeCleanAsync(
            page, nameof(Drawer_renders_groups_rows_live_status_and_storage));
    }

    [Fact]
    public async Task Drawer_selection_deep_link_and_reload_keep_aria_current()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(E2EBrowser.LaunchOptions);
        await using IBrowserContext context = await browser.NewContextAsync(
            new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
            });

        IPage page = await context.NewPageAsync();
        await page.GotoAsync(
            app.BaseUrl,
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

        await Assertions.Expect(row).ToHaveAttributeAsync(
            "aria-current",
            "true",
            new LocatorAssertionsToHaveAttributeOptions { Timeout = 30_000 });
        Assert.Contains(
            "Ranked climb to Diamond",
            await page.Locator("[data-selected-recording]").InnerTextAsync());

        await AssertAxeCleanAsync(
            page, $"{nameof(Drawer_selection_deep_link_and_reload_keep_aria_current)}_selected");

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
        await Assertions.Expect(row).ToHaveAttributeAsync(
            "aria-current",
            "true",
            new LocatorAssertionsToHaveAttributeOptions { Timeout = 30_000 });

        await AssertAxeCleanAsync(
            page, $"{nameof(Drawer_selection_deep_link_and_reload_keep_aria_current)}_reloaded");
    }

    [Fact]
    public async Task Unknown_recording_route_shows_the_message_and_link_back()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(E2EBrowser.LaunchOptions);
        await using IBrowserContext context = await browser.NewContextAsync();

        IPage page = await context.NewPageAsync();
        await page.GotoAsync(
            $"{app.BaseUrl}/r/not-a-recording",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        ILocator unknown = page.Locator("[data-unknown-recording]");
        await unknown.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
        });

        Assert.Contains("Recording not found", await unknown.InnerTextAsync());
        Assert.Equal("/", await unknown.Locator("a").GetAttributeAsync("href"));
        Assert.Contains("Back to library", await unknown.Locator("a").InnerTextAsync());

        await AssertAxeCleanAsync(
            page, nameof(Unknown_recording_route_shows_the_message_and_link_back));
    }

    [Fact]
    public async Task Storage_meter_updates_live_without_a_reload()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(E2EBrowser.LaunchOptions);
        await using IBrowserContext context = await browser.NewContextAsync();

        IPage page = await context.NewPageAsync();
        await page.GotoAsync(
            app.BaseUrl,
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

        await AssertAxeCleanAsync(
            page, nameof(Storage_meter_updates_live_without_a_reload));
    }

    [Fact]
    public async Task Details_card_chapters_and_playlist_copy_work_across_recording_swaps()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(E2EBrowser.LaunchOptions);

        string testName = nameof(Details_card_chapters_and_playlist_copy_work_across_recording_swaps);
        string tracePath = Path.Combine(app.ArtifactsDirectory, $"{testName}.trace.zip");
        string screenshotPath = Path.Combine(app.ArtifactsDirectory, $"{testName}.png");

        await using IBrowserContext context = await browser.NewContextAsync(
            new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
            });
        await context.GrantPermissionsAsync(
            ["clipboard-read", "clipboard-write"],
            new BrowserContextGrantPermissionsOptions { Origin = app.BaseUrl });
        await context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true,
        });

        try
        {
            IPage page = await context.NewPageAsync();
            string? playlistUrl = null;
            page.Request += (_, request) =>
            {
                if (request.Url.Contains("/media/", StringComparison.Ordinal) &&
                    request.Url.EndsWith("/index.m3u8", StringComparison.Ordinal))
                {
                    playlistUrl ??= request.Url;
                }
            };

            await page.GotoAsync(
                app.BaseUrl,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.Locator("input[data-search-input][data-sk-ready]").WaitForAsync();

            ILocator first = page.Locator("nav#sk-drawer a.sk-row", new PageLocatorOptions
            {
                HasText = "Ranked climb to Diamond",
            });
            await first.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 30_000,
            });
            string firstId = (await first.GetAttributeAsync("data-recording-id"))!;
            await first.ClickAsync();
            await page.WaitForURLAsync(
                url => url.EndsWith($"/r/{firstId}", StringComparison.Ordinal),
                new PageWaitForURLOptions { Timeout = 30_000 });

            ILocator details = page.Locator("[data-slot='details'] [data-recording-details]");
            await details.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 30_000,
            });
            await page.Locator("[data-sk='seek'][aria-disabled='false']").WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });

            Assert.Equal(1, await details.CountAsync());
            Assert.Equal(
                "true",
                await details.Locator(".sk-collapse-head").GetAttributeAsync("aria-expanded"));
            Assert.Contains("Recording details", await details.Locator(".sk-collapse-head").InnerTextAsync());
            string date = (await details.Locator("[data-details-date]").InnerTextAsync()).Trim();
            Assert.Contains("2026", date);

            Assert.StartsWith(
                "Ranked climb to Diamond",
                (await details.Locator("[data-details-title]").InnerTextAsync()).Trim());
            Assert.Equal(
                "mock_nova",
                (await details.Locator("[data-details-streamer]").InnerTextAsync()).Trim());
            Assert.Equal(
                "Apex Legends",
                (await details.Locator("[data-details-category]").InnerTextAsync()).Trim());
            Assert.Contains(
                "segments",
                await details.Locator("[data-details-segments]").InnerTextAsync());
            Assert.Equal("1 part", (await details.Locator("[data-details-parts]").InnerTextAsync()).Trim());
            Assert.NotEmpty(await details.Locator("[data-details-size]").InnerTextAsync());

            // The collapsed state is layout preference state and survives a reload.
            await details.Locator(".sk-collapse-head").ClickAsync();
            await Assertions.Expect(details.Locator(".sk-collapse-head")).ToHaveAttributeAsync(
                "aria-expanded",
                "false",
                new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 });
            await page.WaitForFunctionAsync(
                "() => decodeURIComponent(document.cookie).includes('\"details\":false')");

            await page.ReloadAsync(new PageReloadOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
            });
            details = page.Locator("[data-slot='details'] [data-recording-details]");
            await details.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 30_000,
            });
            await Assertions.Expect(details.Locator(".sk-collapse-head")).ToHaveAttributeAsync(
                "aria-expanded",
                "false",
                new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 });
            Assert.Equal(1, await details.CountAsync());

            await details.Locator(".sk-collapse-head").ClickAsync();
            await Assertions.Expect(details.Locator(".sk-collapse-head")).ToHaveAttributeAsync(
                "aria-expanded",
                "true",
                new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 });
            await page.Locator("[data-sk='seek'][aria-disabled='false']").WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });

            // Chapter seek moves the playhead without starting playback and keeps focus on the button.
            ILocator chapter = details.Locator("[data-chapter]").First;
            await chapter.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            double chapterStart = double.Parse(
                (await chapter.GetAttributeAsync("data-chapter"))!, CultureInfo.InvariantCulture);
            Assert.True(await page.Locator("[data-sk='video']").EvaluateAsync<bool>("v => v.paused"));
            await chapter.ClickAsync();
            await page.WaitForFunctionAsync(
                "expected => Math.abs(document.querySelector('[data-sk=video]').currentTime - expected) < 0.5",
                chapterStart,
                new PageWaitForFunctionOptions { Timeout = 10_000 });
            Assert.True(
                await page.Locator("[data-sk='video']").EvaluateAsync<bool>("v => v.paused"),
                "a chapter seek must not autoplay");
            Assert.True(
                await chapter.EvaluateAsync<bool>("b => b === document.activeElement"),
                "focus stays on the chapter button");

            // Copy is public and uses the absolute playlist URL built from the app origin.
            await page.Locator("[data-copy-playlist]").ClickAsync();
            await Assertions.Expect(page.Locator(".sk-snackbar-text")).ToHaveTextAsync(
                "Playlist URL copied",
                new LocatorAssertionsToHaveTextOptions { Timeout = 10_000 });
            Assert.False(string.IsNullOrWhiteSpace(playlistUrl), "the player requested a playlist");
            string copied = await page.EvaluateAsync<string>("navigator.clipboard.readText()");
            Assert.Equal(playlistUrl, copied);

            // Swapping recordings updates the one details card instead of adding another.
            ILocator second = page.Locator("nav#sk-drawer a.sk-row", new PageLocatorOptions
            {
                HasText = "Co-op horror night",
            });
            string secondId = (await second.GetAttributeAsync("data-recording-id"))!;
            await second.ClickAsync();
            await page.WaitForURLAsync(
                url => url.EndsWith($"/r/{secondId}", StringComparison.Ordinal),
                new PageWaitForURLOptions { Timeout = 30_000 });
            await page.WaitForFunctionAsync(
                "() => document.querySelector('[data-details-title]').textContent.trim().startsWith('Co-op horror night')",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            details = page.Locator("[data-recording-details]");
            Assert.Equal(1, await details.CountAsync());
            Assert.Equal(
                "2 parts",
                (await details.Locator("[data-details-parts]").InnerTextAsync()).Trim());
            Assert.Equal(2, await details.Locator("[data-chapter]").CountAsync());

            // A chapter seek while playing moves the playhead and keeps playback running.
            await page.Locator("[data-sk='seek'][aria-disabled='false']").WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });
            await page.Locator("[data-sk='center']").ClickAsync();
            await page.WaitForFunctionAsync(
                "() => document.querySelector('[data-sk=video]').currentTime > 0.5",
                null,
                new PageWaitForFunctionOptions { Timeout = 10_000 });

            ILocator playingChapter = details.Locator("[data-chapter]").Nth(1);
            double playingStart = double.Parse(
                (await playingChapter.GetAttributeAsync("data-chapter"))!, CultureInfo.InvariantCulture);
            await playingChapter.ClickAsync();
            await page.WaitForFunctionAsync(
                "expected => !document.querySelector('[data-sk=video]').paused && " +
                "Math.abs(document.querySelector('[data-sk=video]').currentTime - expected) < 0.5",
                playingStart,
                new PageWaitForFunctionOptions { Timeout = 10_000 });
            Assert.False(await page.Locator("[data-sk='video']").EvaluateAsync<bool>("v => v.paused"));

            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = screenshotPath,
                FullPage = true,
            });

            await AssertAxeCleanAsync(page, testName);
        }
        finally
        {
            await context.Tracing.StopAsync(new TracingStopOptions { Path = tracePath });
        }
    }

    private async Task AssertAxeCleanAsync(IPage page, string name)
    {
        AxeResult results = await page.RunAxe();
        string path = Path.Combine(app.ArtifactsDirectory, $"{name}.axe.json");
        await File.WriteAllTextAsync(path, results.ToString());

        string violations = string.Join(
            Environment.NewLine,
            results.Violations.Select(violation =>
                $"{violation.Id} ({violation.Impact}): {violation.HelpUrl} " +
                $"on {string.Join(", ", violation.Nodes.Select(node => node.Target))}"));

        Assert.True(
            results.Violations.Length == 0,
            $"{name}: axe found {results.Violations.Length} violation(s):" +
            $"{Environment.NewLine}{violations}");
    }
}
