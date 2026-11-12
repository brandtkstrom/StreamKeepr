using System.Text;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace StreamKeepr.E2E;

/// <summary>
/// Visual regression (testing contract §8, plan F9-T4, #106): the main screens at 1280×820
/// and 375×812 against approved baselines in <c>tests/StreamKeepr.E2E/Visual/{os}</c>.
/// Chrome draws text differently on each OS, so baselines are per OS, and only the Linux ones
/// (from CI) are committed. Every run writes its screenshots to <c>visual/actual</c> in the
/// artifacts. Until an OS has any baseline, its screens are reported as blocked (testing
/// contract §10) and do not fail. Once it has one, a missing baseline fails, and so does a
/// screen with more than 0.1 % differing pixels.
/// </summary>
[Trait("Category", "E2E")]
public sealed class VisualJourneyTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly ViewportSize Desktop = new() { Width = 1280, Height = 820 };
    private static readonly ViewportSize Phone = new() { Width = 375, Height = 812 };
    private const string MaskColor = "#FF00FF";
    private const string SearchReady = "input[data-search-input][data-sk-ready]";
    private const string DetailsCard = "[data-slot='details'] [data-recording-details]";
    private const int SettledFrames = 10;

    // The live feed is off so nothing changes the library between screenshots.
    private readonly MockAppFixture _app = new(
        "visual", new Dictionary<string, string> { ["Mock__Feed__Enabled"] = "false" });

    public Task InitializeAsync() => _app.InitializeAsync();

    public Task DisposeAsync() => _app.DisposeAsync();

    /// <summary>One screen to capture: how to reach it and which part to keep.</summary>
    private sealed record Screen(
        string Name, ViewportSize Viewport, Func<IPage, Task> OpenAsync, string? Element = null);

    private IEnumerable<Screen> Screens()
    {
        foreach ((string size, ViewportSize viewport) in new[] { ("desktop", Desktop), ("phone", Phone) })
        {
            yield return new($"home-{size}", viewport, OpenHomeAsync);
            yield return new($"recording-{size}", viewport, OpenRecordingAsync);
            yield return new($"details-{size}", viewport, OpenRecordingAsync, DetailsCard);
            yield return new($"settings-{size}", viewport, OpenSettingsAsync);
        }

        yield return new("drawer-phone", Phone, OpenPhoneDrawerAsync);
    }

    /// <summary>
    /// The regions that change between runs for reasons other than the UI. This list is the
    /// source of truth; <c>Visual/README.md</c> describes it.
    /// </summary>
    private static IReadOnlyList<ILocator> Masks(IPage page) =>
    [
        // Video frames depend on decode timing; the buffered fill on how far hls.js has loaded.
        page.Locator("video"),
        page.Locator("[data-sk='buffered']"),

        // "Today" and "Yesterday" follow the wall clock.
        page.Locator("nav#sk-drawer .sk-day"),

        // Sessions still recording keep growing, and startup recovery decides between
        // Recording and Reconnecting. Their title stays visible.
        page.Locator("nav#sk-drawer a.sk-row:is([data-state='rec'], [data-state='reconnecting']) :is(.sk-thumb, .sk-row-meta, .sk-status)"),
        page.Locator("nav#sk-drawer .sk-group-head:has(.sk-live-ring) .meta"),
        page.Locator("[data-channel-state]"),
    ];

    [Fact]
    public async Task Main_screens_match_their_approved_baselines()
    {
        string os = OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "windows";
        string baselineDirectory = Path.Combine(
            _app.RepositoryRoot, "tests", "StreamKeepr.E2E", "Visual", os);
        bool hasBaselines = Directory.Exists(baselineDirectory) &&
            Directory.EnumerateFiles(baselineDirectory, "*.png").Any();
        string actualDirectory = Path.Combine(_app.ArtifactsDirectory, "actual");
        string diffDirectory = Path.Combine(_app.ArtifactsDirectory, "diff");
        Directory.CreateDirectory(actualDirectory);

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(E2EBrowser.LaunchOptions);
        IPage diffPage = await browser.NewPageAsync();

        var report = new StringBuilder();
        var failures = new List<string>();
        void Report(string screen, string line, bool failed = false)
        {
            report.AppendLine($"{screen,-20} {line}");
            output.WriteLine($"{screen,-20} {line}");
            if (failed)
                failures.Add($"{screen}: {line}");
        }

        foreach (Screen screen in Screens())
        {
            byte[] actual = await CaptureAsync(browser, screen);
            await File.WriteAllBytesAsync(Path.Combine(actualDirectory, $"{screen.Name}.png"), actual);

            string baselinePath = Path.Combine(baselineDirectory, $"{screen.Name}.png");
            if (!File.Exists(baselinePath))
            {
                // Before the first approval the whole OS is blocked; after it, a missing
                // baseline means one was deleted or renamed, which must not pass quietly.
                Report(
                    screen.Name,
                    hasBaselines
                        ? $"FAIL: no baseline, although other {os} baselines exist"
                        : $"blocked: no approved {os} baselines yet; review visual/actual/{screen.Name}.png",
                    failed: hasBaselines);
                continue;
            }

            VisualDiff diff = await VisualDiff.CompareAsync(diffPage, await File.ReadAllBytesAsync(baselinePath), actual);
            if (diff.DiffPng is not null)
            {
                Directory.CreateDirectory(diffDirectory);
                await File.WriteAllBytesAsync(Path.Combine(diffDirectory, $"{screen.Name}.png"), diff.DiffPng);
            }

            string result = diff.SizeMatches
                ? $"{diff.DifferentRatio:P3} of pixels differ ({diff.DifferentPixels:N0})"
                : $"size {diff.Width}×{diff.Height} differs from the baseline";
            Report(screen.Name, $"{(diff.Exceeds() ? "FAIL" : "ok")}: {result}", diff.Exceeds());
        }

        await File.WriteAllTextAsync(Path.Combine(_app.ArtifactsDirectory, "report.txt"), report.ToString());
        Assert.True(
            failures.Count == 0,
            $"Visual check failed (budget {VisualDiff.MaxDifferentRatio:P1}); see visual/report.txt and visual/diff:" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    /// <summary>
    /// Opens <paramref name="screen"/> in a fresh context. This does not reuse
    /// <c>LibraryJourneyTests.OpenAsync</c> because the screenshots also pin the locale, time
    /// zone, colour scheme and motion.
    /// </summary>
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
        await GoToAsync(page, "/");
        await screen.OpenAsync(page);
        await page.EvaluateAsync("() => document.fonts.ready");
        await WaitForLayoutToSettleAsync(page);

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

    /// <summary>Navigates within the app and waits until the circuit is interactive.</summary>
    private async Task GoToAsync(IPage page, string path)
    {
        await page.GotoAsync(
            $"{_app.BaseUrl}{path}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator(SearchReady).WaitForAsync();
    }

    /// <summary>
    /// Waits until the page holds still, so masks land where they belong. Two things move
    /// after the circuit starts: rows are measured only once drawn (<c>content-visibility</c>),
    /// and the drawer scrolls the selected row into view. Wait until no element's box changes
    /// for <see cref="SettledFrames"/> frames, then centre the selected row (when the drawer is
    /// on screen) and wait again, so every run captures the same scroll position.
    /// </summary>
    private static Task WaitForLayoutToSettleAsync(IPage page) =>
        page.EvaluateAsync("""
            async settledFrames => {
              const maxFrames = 600;
              const frame = () => new Promise(requestAnimationFrame);
              const layout = () => Array.from(document.querySelectorAll('body *'), el => {
                const box = el.getBoundingClientRect();
                return `${Math.round(box.x)},${Math.round(box.y)},${Math.round(box.width)},${Math.round(box.height)}`;
              }).join(';');
              const settle = async step => {
                let last = null;
                for (let still = 0, frames = 0; still < settledFrames; frames++) {
                  if (frames > maxFrames) throw new Error(`the layout kept moving for ${maxFrames} frames`);
                  step?.();
                  await frame();
                  const now = layout();
                  still = now === last ? still + 1 : 0;
                  last = now;
                }
              };

              await settle();
              const list = document.querySelector('nav#sk-drawer .sk-drawer-list');
              const row = list?.querySelector("a.sk-row[aria-current='true']");
              const view = list?.getBoundingClientRect();
              if (row && list.checkVisibility({ visibilityProperty: true }) &&
                  view.height > 0 && view.right > 0 && view.left < innerWidth) {
                await settle(() => row.scrollIntoView({ block: 'center' }));
              }
            }
            """, SettledFrames);

    private static Task OpenHomeAsync(IPage page) =>
        page.Locator("nav#sk-drawer .sk-group").First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Attached });

    private async Task OpenRecordingAsync(IPage page)
    {
        ILocator row = page.Locator("nav#sk-drawer a.sk-row", new PageLocatorOptions { HasText = "Ranked climb to Diamond" });
        string id = await row.GetAttributeAsync("data-recording-id")
            ?? throw new InvalidOperationException("The seeded \"Ranked climb to Diamond\" row has no recording id.");
        await GoToAsync(page, $"/r/{id}");
        await page.Locator(DetailsCard).WaitForAsync();
        await page.Locator("[data-sk='seek'][aria-disabled='false']").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });
    }

    private async Task OpenSettingsAsync(IPage page)
    {
        await GoToAsync(page, "/settings");
        await page.GetByRole(AriaRole.Heading, new() { Name = "Settings" }).WaitForAsync();
        await page.Locator("[data-channel]").First.WaitForAsync();
    }

    private static async Task OpenPhoneDrawerAsync(IPage page)
    {
        await page.Locator("[data-drawer-toggle]").ClickAsync();
        await page.Locator(".sk-shell.drawer-modal-open").WaitForAsync();
    }
}
