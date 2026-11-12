using System.Text;
using Microsoft.Playwright;

namespace StreamKeepr.E2E;

/// <summary>
/// Visual regression (testing contract §8, plan F9-T4, #106): the main screens at 1280×820
/// and 375×812 against approved baselines in <c>tests/StreamKeepr.E2E/Visual/{os}</c>.
/// Chrome draws text differently on each OS, so baselines are per OS, and only the Linux ones
/// (from CI) are committed. Every run writes its screenshots to <c>visual/actual</c> in the
/// artifacts. A screen with no approved baseline for this OS is reported as blocked in
/// <c>visual/report.txt</c> and does not fail (testing contract §8); with a baseline, more
/// than 0.1 % differing pixels fails.
/// </summary>
[Trait("Category", "E2E")]
public sealed class VisualJourneyTests : IAsyncLifetime
{
    private static readonly ViewportSize Desktop = new() { Width = 1280, Height = 820 };
    private static readonly ViewportSize Phone = new() { Width = 375, Height = 812 };
    private const string MaskColor = "#FF00FF";

    // The live feed is off so nothing changes the library between screenshots.
    private readonly MockAppFixture _app = new(
        "visual", new Dictionary<string, string> { ["Mock__Feed__Enabled"] = "false" });

    public Task InitializeAsync() => _app.InitializeAsync();

    public Task DisposeAsync() => _app.DisposeAsync();

    /// <summary>One screen to capture: how to reach it and which part to keep.</summary>
    private sealed record Screen(
        string Name, ViewportSize Viewport, Func<IPage, Task> OpenAsync, string? Element = null);

    private static IEnumerable<Screen> Screens()
    {
        foreach ((string size, ViewportSize viewport) in new[] { ("desktop", Desktop), ("phone", Phone) })
        {
            yield return new($"home-{size}", viewport, OpenHomeAsync);
            yield return new($"recording-{size}", viewport, OpenRecordingAsync);
            yield return new($"details-{size}", viewport, OpenRecordingAsync, "[data-slot='details'] [data-recording-details]");
            yield return new($"settings-{size}", viewport, OpenSettingsAsync);
        }

        yield return new("drawer-phone", Phone, OpenPhoneDrawerAsync);
    }

    /// <summary>
    /// Regions that change between runs for reasons other than the UI: relative day labels
    /// and capture times follow the wall clock, the live captures keep growing, and video
    /// frames depend on decode timing.
    /// </summary>
    private static IReadOnlyList<ILocator> Masks(IPage page) =>
    [
        page.Locator("video"),
        page.Locator("nav#sk-drawer .sk-day"),
        page.Locator("nav#sk-drawer a.sk-row:is([data-state='rec'], [data-state='reconnecting'])"),
        page.Locator("nav#sk-drawer .sk-group-head .meta"),
        page.Locator(".sk-storage"),
        page.Locator("[data-details-date]"),
    ];

    [Fact]
    public async Task Main_screens_match_their_approved_baselines()
    {
        string os = OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "windows";
        string baselineDirectory = Path.Combine(
            _app.RepositoryRoot, "tests", "StreamKeepr.E2E", "Visual", os);
        string actualDirectory = Path.Combine(_app.ArtifactsDirectory, "actual");
        string diffDirectory = Path.Combine(_app.ArtifactsDirectory, "diff");
        Directory.CreateDirectory(actualDirectory);

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(E2EBrowser.LaunchOptions);
        IPage diffPage = await browser.NewPageAsync();

        var report = new StringBuilder();
        var failures = new List<string>();
        foreach (Screen screen in Screens())
        {
            byte[] actual = await CaptureAsync(browser, screen);
            await File.WriteAllBytesAsync(Path.Combine(actualDirectory, $"{screen.Name}.png"), actual);

            string baselinePath = Path.Combine(baselineDirectory, $"{screen.Name}.png");
            if (!File.Exists(baselinePath))
            {
                report.AppendLine($"{screen.Name,-20} blocked: no approved {os} baseline; review visual/actual/{screen.Name}.png");
                continue;
            }

            VisualDiff diff = await VisualDiff.CompareAsync(diffPage, await File.ReadAllBytesAsync(baselinePath), actual);
            string result = diff.SizeMatches
                ? $"{diff.DifferentRatio:P3} of pixels differ ({diff.DifferentPixels:N0})"
                : $"size {diff.Width}×{diff.Height} differs from the baseline";
            if (diff.DiffPng is not null)
            {
                Directory.CreateDirectory(diffDirectory);
                await File.WriteAllBytesAsync(Path.Combine(diffDirectory, $"{screen.Name}.png"), diff.DiffPng);
            }

            bool failed = diff.Exceeds(VisualDiff.MaxDifferentRatio);
            report.AppendLine($"{screen.Name,-20} {(failed ? "FAIL" : "ok")}: {result}");
            if (failed)
                failures.Add($"{screen.Name}: {result}");
        }

        await File.WriteAllTextAsync(Path.Combine(_app.ArtifactsDirectory, "report.txt"), report.ToString());
        Assert.True(
            failures.Count == 0,
            $"Screens differ from their baselines (budget {VisualDiff.MaxDifferentRatio:P1}); see visual/diff:{Environment.NewLine}" +
            string.Join(Environment.NewLine, failures));
    }

    private async Task<byte[]> CaptureAsync(IBrowser browser, Screen screen)
    {
        await using IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = screen.Viewport,
            Locale = "en-US",
            TimezoneId = "UTC",
            ColorScheme = ColorScheme.Light,
            ReducedMotion = ReducedMotion.Reduce,
        });
        IPage page = await context.NewPageAsync();
        await page.GotoAsync(_app.BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("input[data-search-input][data-sk-ready]").WaitForAsync();
        await screen.OpenAsync(page);
        await page.EvaluateAsync("() => document.fonts.ready");
        await WaitForDrawerToSettleAsync(page);

        return screen.Element is null
            ? await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Animations = ScreenshotAnimations.Disabled,
                Caret = ScreenshotCaret.Hide,
                Mask = Masks(page),
                MaskColor = MaskColor,
            })
            : await page.Locator(screen.Element).ScreenshotAsync(new LocatorScreenshotOptions
            {
                Animations = ScreenshotAnimations.Disabled,
                Caret = ScreenshotCaret.Hide,
                Mask = Masks(page),
                MaskColor = MaskColor,
            });
    }

    /// <summary>
    /// The drawer scrolls the selected row into view after the circuit starts. Wait until its
    /// scroll position holds for ten frames so it is not captured mid-scroll.
    /// </summary>
    private static Task WaitForDrawerToSettleAsync(IPage page) =>
        page.EvaluateAsync("""
            async () => {
              const list = document.querySelector('nav#sk-drawer .sk-drawer-list');
              if (!list) return;
              const frame = () => new Promise(requestAnimationFrame);
              let last = list.scrollTop;
              for (let still = 0; still < 10;) {
                await frame();
                still = list.scrollTop === last ? still + 1 : 0;
                last = list.scrollTop;
              }
            }
            """);

    private static Task OpenHomeAsync(IPage page) =>
        page.Locator("nav#sk-drawer .sk-group").First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Attached });

    private static async Task OpenRecordingAsync(IPage page)
    {
        ILocator row = page.Locator("nav#sk-drawer a.sk-row", new PageLocatorOptions { HasText = "Ranked climb to Diamond" });
        string id = (await row.GetAttributeAsync("data-recording-id"))!;
        await page.GotoAsync($"{new Uri(page.Url).GetLeftPart(UriPartial.Authority)}/r/{id}");
        await page.Locator("input[data-search-input][data-sk-ready]").WaitForAsync();
        await page.Locator("[data-slot='details'] [data-recording-details]").WaitForAsync();
        await page.Locator("[data-sk='seek'][aria-disabled='false']").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });
    }

    private static async Task OpenSettingsAsync(IPage page)
    {
        await page.GotoAsync($"{new Uri(page.Url).GetLeftPart(UriPartial.Authority)}/settings");
        await page.Locator("input[data-search-input][data-sk-ready]").WaitForAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Settings" }).WaitForAsync();
    }

    private static async Task OpenPhoneDrawerAsync(IPage page)
    {
        await page.Locator("[data-drawer-toggle]").ClickAsync();
        await page.Locator(".sk-shell.drawer-modal-open").WaitForAsync();
    }
}
