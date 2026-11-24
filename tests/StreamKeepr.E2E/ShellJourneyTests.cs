using System.Globalization;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

[Collection(MockAppCollection.Name)]
[CaptureFailure]
[Trait("Category", "E2E")]
public sealed class ShellJourneyTests(MockAppFixture app)
{
    [Fact]
    public async Task App_loads_shell_renders_and_axe_is_clean()
    {
        string testName = nameof(App_loads_shell_renders_and_axe_is_clean);
        string screenshotPath = Path.Combine(app.ArtifactsDirectory, $"{testName}.png");

        await using Journey session = await Journey.OpenAsync(
            app, app.BaseUrl, new ViewportSize { Width = 1280, Height = 720 });

        IPage page = session.Page;

        Assert.Equal("Stream Keepr", await page.TitleAsync());

        ILocator shell = page.Locator("[data-shell]");
        await shell.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
        });

        Assert.True(await page.GetByRole(AriaRole.Banner).IsVisibleAsync());
        Assert.Contains("StreamKeepr", await page.Locator("header a[aria-label='StreamKeepr home']").InnerTextAsync());
        Assert.True(await page.Locator(LibraryHooks.Drawer).IsVisibleAsync());

        ILocator main = page.Locator("main");
        await main.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached,
        });
        // The library page is not a watch page: the player, details and stats slots exist
        // only on /r/{id} (#175).
        Assert.Equal(0, await main.Locator("[data-slot]").CountAsync());
        Assert.Equal(
            "24px",
            await page.EvaluateAsync<string>(
                "getComputedStyle(document.querySelector('main')).paddingTop"));

        // The shell must be styled, not just parsed: an empty static-asset body would
        // still leave the DOM present while axe color-contrast checks see no CSS.
        await page.WaitForFunctionAsync(
            "getComputedStyle(document.querySelector('h1')).position === 'absolute'");
        Assert.Equal(
            "absolute",
            await page.EvaluateAsync<string>(
                "getComputedStyle(document.querySelector('h1')).position"));

        IReadOnlyList<BrowserContextCookiesResult> cookies = await session.Context.CookiesAsync();
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

    [Fact]
    public async Task Wordmark_returns_home_clears_query_and_exits_theater()
    {
        await using Journey session = await OpenAsync(app);
        IPage page = session.Page;

        await page.Locator("input[data-search-input]").FillAsync("NIGHT");
        await Journey.WaitForUrlAsync(page, url => url.Contains("q=NIGHT", StringComparison.Ordinal));
        await session.OpenRecordingAsync(await LibraryHooks.RowAsync(page));

        await page.Locator("[data-sk='theater']").ClickAsync();
        await page.Locator("[data-shell][data-theater-mode]").WaitForAsync();

        await page.Locator("a[aria-label='StreamKeepr home']").ClickAsync();
        await Journey.WaitForUrlAsync(page, url => new Uri(url).AbsolutePath == "/");

        await page.Locator("[data-shell]:not([data-theater-mode])").WaitForAsync();
        Assert.Equal("", await page.Locator("input[data-search-input]").InputValueAsync());
        Assert.Equal("newest", await page.Locator("[data-sort]").GetAttributeAsync("data-sort"));
        Assert.True(await page.Locator(LibraryHooks.Drawer).IsVisibleAsync());

        await AssertAxeCleanAsync(page, "shell-wordmark-home");
    }

    [Fact]
    public async Task Drawer_renders_groups_rows_live_status_and_storage()
    {
        await using Journey session = await Journey.OpenAsync(
            app, app.BaseUrl, new ViewportSize { Width = 1280, Height = 720 });
        IPage page = session.Page;

        await page.Locator(LibraryHooks.Channel).First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        Assert.True(await page.Locator(LibraryHooks.Channel).CountAsync() >= 4);
        Assert.True(await LibraryJourneyTests.TotalRecordingsAsync(page) >= 7);

        await page.Locator("[data-rec-dot]").WaitForAsync(
            new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 30_000,
            });

        // A recording channel sorts to the top; its column shows the live row.
        await Assertions.Expect(page.Locator(LibraryHooks.Channel).First)
            .ToContainTextAsync(
                "Recording now",
                new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await LibraryHooks.OpenChannelAsync(
            page, (await page.Locator(LibraryHooks.Channel).First.GetAttributeAsync("data-library-channel"))!);
        await page.Locator($"{LibraryHooks.Row} [data-status=rec]").First.WaitForAsync(
            new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 30_000,
            });

        string href = await (await LibraryHooks.RowAsync(page)).GetAttributeAsync("href") ?? "";
        Assert.StartsWith("/r/", href);

        ILocator storage = page.Locator("[data-storage]");
        Assert.True(await storage.IsVisibleAsync());
        Assert.Contains("free of", await storage.GetAttributeAsync("title") ?? "");

        await AssertAxeCleanAsync(
            page, nameof(Drawer_renders_groups_rows_live_status_and_storage));
    }

    [Fact]
    public async Task Drawer_selection_deep_link_and_reload_keep_aria_current()
    {
        await using Journey session = await Journey.OpenAsync(
            app, app.BaseUrl, new ViewportSize { Width = 1280, Height = 720 });
        IPage page = session.Page;

        // The drawer row: Home's cards carry the same hook (#182).
        await page.Locator("input[data-search-input][data-sk-ready]").WaitForAsync();
        ILocator row = await LibraryHooks.RowAsync(page, LibraryHooks.Row, "Ranked climb to Diamond");

        string id = await row.GetAttributeAsync("data-recording-id") ?? "";
        await row.ClickAsync();
        await Journey.WaitForUrlAsync(page, url => new Uri(url).AbsolutePath == $"/r/{id}", 30_000);
        await ExpandLibraryOnWatchPageAsync(page);

        // Picking it closed the column; reopened, the row is marked current.
        row = await LibraryHooks.RowAsync(page, LibraryHooks.Row, "Ranked climb to Diamond");
        await Assertions.Expect(row).ToHaveAttributeAsync(
            "aria-current",
            "true",
            new LocatorAssertionsToHaveAttributeOptions { Timeout = 30_000 });
        Assert.Contains(
            "Ranked climb to Diamond",
            await page.Locator("[data-selected-recording]").InnerTextAsync());

        await AssertAxeCleanAsync(
            page, $"{nameof(Drawer_selection_deep_link_and_reload_keep_aria_current)}_selected");

        await session.ReloadAsync();

        await page.Locator("input[data-search-input][data-sk-ready]").WaitForAsync();
        row = await LibraryHooks.RowAsync(page, LibraryHooks.Row, "Ranked climb to Diamond");
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
        await using Journey session = await Journey.OpenAsync(
            app, $"{app.BaseUrl}/r/not-a-recording", ready: JourneyReady.Shell);
        IPage page = session.Page;

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
        await using Journey session = await Journey.OpenAsync(app, app.BaseUrl);
        IPage page = session.Page;

        // The full meter is in the open library; the rail shows its icon.
        await LibraryHooks.OpenLibraryAsync(page);
        ILocator storage = page.Locator("[data-storage]");
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
        string testName = nameof(Details_card_chapters_and_playlist_copy_work_across_recording_swaps);
        string screenshotPath = Path.Combine(app.ArtifactsDirectory, $"{testName}.png");

        await using Journey session = await Journey.OpenAsync(
            app, app.BaseUrl, new ViewportSize { Width = 1280, Height = 720 });
        await session.Context.GrantPermissionsAsync(
            ["clipboard-read", "clipboard-write"],
            new BrowserContextGrantPermissionsOptions { Origin = app.BaseUrl });

        IPage page = session.Page;
        string? playlistUrl = null;
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("/media/", StringComparison.Ordinal) &&
                request.Url.EndsWith("/index.m3u8", StringComparison.Ordinal))
            {
                playlistUrl ??= request.Url;
            }
        };

        ILocator first = await LibraryHooks.RowAsync(page, LibraryHooks.Row, "Ranked climb to Diamond");
        string firstId = (await first.GetAttributeAsync("data-recording-id"))!;
        await first.ClickAsync();
        await Journey.WaitForUrlAsync(
            page, url => url.EndsWith($"/r/{firstId}", StringComparison.Ordinal), 30_000);
        await ExpandLibraryOnWatchPageAsync(page);

        ILocator details = page.Locator("[data-slot='details'] [data-recording-details]");
        // The collapsible panel under the title block is Chapters; the title block itself
        // sits on the page and does not collapse.
        ILocator chapters = page.Locator("[data-slot='details'] [data-chapters-panel]");
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
            await chapters.Locator("[data-collapsible-toggle]").GetAttributeAsync("aria-expanded"));
        Assert.Contains("Chapters", await chapters.Locator("[data-collapsible-toggle]").InnerTextAsync());
        string date = (await details.Locator("[data-details-start]").InnerTextAsync()).Trim();
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
        await chapters.Locator("[data-collapsible-toggle]").ClickAsync();
        await Assertions.Expect(chapters.Locator("[data-collapsible-toggle]")).ToHaveAttributeAsync(
            "aria-expanded",
            "false",
            new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 });
        await page.WaitForFunctionAsync(
            "() => decodeURIComponent(document.cookie).includes('\"details\":false')");

        await session.ReloadAsync();
        details = page.Locator("[data-slot='details'] [data-recording-details]");
        await details.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });
        await Assertions.Expect(chapters.Locator("[data-collapsible-toggle]")).ToHaveAttributeAsync(
            "aria-expanded",
            "false",
            new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 });
        Assert.Equal(1, await details.CountAsync());

        await chapters.Locator("[data-collapsible-toggle]").ClickAsync();
        await Assertions.Expect(chapters.Locator("[data-collapsible-toggle]")).ToHaveAttributeAsync(
            "aria-expanded",
            "true",
            new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 });
        await page.Locator("[data-sk='seek'][aria-disabled='false']").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });

        // Chapter seek moves the playhead without starting playback and keeps focus on the button.
        ILocator chapter = chapters.Locator("[data-chapter]").First;
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
        await Feedback.Snackbar(page, "Playlist URL copied").WaitForAsync(new() { Timeout = 10_000 });
        Assert.False(string.IsNullOrWhiteSpace(playlistUrl), "the player requested a playlist");
        string copied = await page.EvaluateAsync<string>("navigator.clipboard.readText()");
        Assert.Equal(playlistUrl, copied);

        // Swapping recordings updates the one details card instead of adding another.
        ILocator second = await LibraryHooks.RowAsync(page, LibraryHooks.Row, "Co-op horror night");
        string secondId = (await second.GetAttributeAsync("data-recording-id"))!;
        await second.ClickAsync();
        await Journey.WaitForUrlAsync(
            page, url => url.EndsWith($"/r/{secondId}", StringComparison.Ordinal), 30_000);
        await page.WaitForFunctionAsync(
            "() => document.querySelector('[data-details-title]').textContent.trim().startsWith('Co-op horror night')",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        details = page.Locator("[data-recording-details]");
        Assert.Equal(1, await details.CountAsync());
        Assert.Equal(
            "2 parts",
            (await details.Locator("[data-details-parts]").InnerTextAsync()).Trim());
        Assert.Equal(2, await chapters.Locator("[data-chapter]").CountAsync());

        // A chapter seek while playing moves the playhead and keeps playback running.
        await page.Locator("[data-sk='seek'][aria-disabled='false']").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });
        await page.Locator("[data-sk='center']").ClickAsync();
        await page.WaitForFunctionAsync(
            "() => document.querySelector('[data-sk=video]').currentTime > 0.5",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });

        ILocator playingChapter = chapters.Locator("[data-chapter]").Nth(1);
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

    private async Task AssertAxeCleanAsync(IPage page, string name)
    {
        await Journey.WaitForReconnectAsync(page);
        AxeResult results = await page.RunAxe();
        // Only a failing check leaves its axe output behind.
        if (results.Violations.Length > 0)
        {
            await File.WriteAllTextAsync(
                Path.Combine(app.ArtifactsDirectory, $"{name}.axe.json"), results.ToString());
        }

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
