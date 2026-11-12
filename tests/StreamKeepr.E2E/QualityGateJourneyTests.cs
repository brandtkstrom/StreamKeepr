using System.IO.Compression;
using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

/// <summary>
/// Release gates from the testing contract §8: the first-paint JS budget and reduced motion.
/// </summary>
[Collection(MockAppCollection.Name)]
[Trait("Category", "E2E")]
public sealed class QualityGateJourneyTests(MockAppFixture app)
{
    /// <summary>First-paint JS budget, gzipped, including <c>blazor.web.js</c> (F9-T7).</summary>
    private const long FirstPaintBudgetBytes = 200 * 1024;

    [Fact]
    public async Task First_paint_js_fits_the_budget_and_the_player_loads_only_on_recording_pages()
    {
        IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
        await using IBrowserContext context = await browser.NewContextAsync(
            new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1280, Height = 820 } });
        IPage page = await context.NewPageAsync();

        var scripts = new List<IResponse>();
        page.Response += (_, response) =>
        {
            if (response.Request.ResourceType == "script")
                scripts.Add(response);
        };

        // The home page with no recording selected is the first paint; wait for the circuit
        // and every module the interactive shell imports.
        await page.GotoAsync(app.BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.Locator("nav#sk-drawer a.sk-row").First.WaitForAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // A module may be both preloaded and imported; the browser downloads it once.
        IResponse[] firstPaint = scripts.DistinctBy(response => response.Url).ToArray();
        var sizes = new List<(string Url, long Gzipped)>();
        foreach (IResponse response in firstPaint)
            sizes.Add((response.Url, Gzipped(await response.BodyAsync())));

        long total = sizes.Sum(size => size.Gzipped);
        string report = string.Join(
            Environment.NewLine,
            sizes.OrderByDescending(size => size.Gzipped).Select(size => $"{size.Gzipped,8:N0}  {size.Url}"));
        await File.WriteAllTextAsync(
            Path.Combine(app.ArtifactsDirectory, "first-paint-js.txt"),
            $"{total:N0} bytes gzipped (budget {FirstPaintBudgetBytes:N0}){Environment.NewLine}{report}");

        Assert.Contains(sizes, size => size.Url.Contains("blazor.web", StringComparison.Ordinal));
        Assert.True(
            total <= FirstPaintBudgetBytes,
            $"First-paint JS is {total:N0} bytes gzipped, over the {FirstPaintBudgetBytes:N0} budget:{Environment.NewLine}{report}");
        Assert.DoesNotContain(sizes, size => IsPlayerScript(size.Url));

        // Opening a recording is what brings in the player and hls.js.
        await page.Locator("nav#sk-drawer a.sk-row[data-state='saved']").First.ClickAsync();
        await page.Locator("video").WaitForAsync();
        await Expect(() => Task.FromResult(scripts.Any(response => response.Url.Contains("hls", StringComparison.Ordinal)) ? 1 : 0), 1);
        Assert.Contains(scripts, response => response.Url.Contains("/Features/Player/VideoPlayer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reduced_motion_stops_the_rec_pulse_fades_and_collapse_animations()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator recDot = page.Locator(".sk-status.rec .dot").First;
        await recDot.WaitForAsync();

        // Sanity: without the preference the REC dot pulses.
        Assert.Equal("sk-pulse", await recDot.EvaluateAsync<string>("e => getComputedStyle(e).animationName"));

        await page.EmulateMediaAsync(new PageEmulateMediaOptions { ReducedMotion = ReducedMotion.Reduce });
        Assert.Equal("none", await recDot.EvaluateAsync<string>("e => getComputedStyle(e).animationName"));
        await AssertNoMotionAsync(page, "home");

        // The recording page brings the player chrome, the details collapsible, and tooltips.
        await page.Locator("nav#sk-drawer a.sk-row[data-state='saved']").First.ClickAsync();
        await page.Locator("[data-recording-details]").WaitForAsync();
        await AssertNoMotionAsync(page, "recording");

        await page.GotoAsync($"{app.BaseUrl}/settings");
        await page.Locator("main").WaitForAsync();
        await AssertNoMotionAsync(page, "settings");
    }

    [Fact]
    public async Task Keyboard_alone_reaches_every_control_on_each_screen()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        await page.Locator("nav#sk-drawer a.sk-row").First.WaitForAsync();
        await AssertTabReachesEveryControlAsync(page, "home");

        // Rows are one roving tab stop (arrow keys move inside the tree), so the recording page
        // is opened from the keyboard too.
        await page.Locator("[role='tree'] [role='treeitem'][tabindex='0']").FocusAsync();
        await page.Keyboard.PressAsync("Home");
        await page.Keyboard.PressAsync("ArrowDown");
        await page.Keyboard.PressAsync("Enter");
        await page.Locator("[data-recording-details]").WaitForAsync();
        // Wait for a playable source: controls such as Go live appear only once it has loaded.
        await page.Locator("[data-sk='seek'][aria-disabled='false']").WaitForAsync(new() { Timeout = 30_000 });
        await AssertTabReachesEveryControlAsync(page, "recording");
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "keyboard-recording");

        await page.GotoAsync($"{app.BaseUrl}/settings");
        await page.Locator("[data-channel]").First.WaitForAsync();
        await AssertTabReachesEveryControlAsync(page, "settings");
    }

    /// <summary>
    /// Presses Tab from the top of the page until focus comes round again, then fails if any
    /// visible, enabled control with a tab stop was never focused. Roving items
    /// (<c>tabindex="-1"</c>) are reached with arrow keys and are not tab stops.
    /// </summary>
    private static async Task AssertTabReachesEveryControlAsync(IPage page, string screen)
    {
        await page.EvaluateAsync(
            "() => { document.querySelectorAll('[data-sk-reached]').forEach(e => e.removeAttribute('data-sk-reached')); document.activeElement?.blur(); }");

        int stops = 0;
        for (int press = 0; press < 400; press++)
        {
            await page.Keyboard.PressAsync("Tab");
            bool seen = await page.EvaluateAsync<bool>(
                """
                () => {
                  const e = document.activeElement;
                  if (!e || e === document.body) return false;
                  if (e.hasAttribute('data-sk-reached')) return true;
                  e.setAttribute('data-sk-reached', '');
                  return false;
                }
                """);
            if (seen)
                break;
            stops++;
        }

        string[] unreached = await page.EvaluateAsync<string[]>(
            """
            () => [...document.querySelectorAll('a[href], button, input, select, textarea, summary, [tabindex]')]
              .filter(e => !e.disabled
                && e.getAttribute('tabindex') !== '-1'
                && !e.closest('[inert]')
                && e.checkVisibility({ visibilityProperty: true })
                && !e.hasAttribute('data-sk-reached'))
              .map(e => e.outerHTML.slice(0, 160))
            """);

        Assert.True(stops > 0, $"Tab never moved focus on {screen}");
        Assert.True(
            unreached.Length == 0,
            $"Tab never reached these controls on {screen}:{Environment.NewLine}{string.Join(Environment.NewLine, unreached)}");
    }

    /// <summary>
    /// Under reduced motion no element transitions and nothing animates, except busy spinners,
    /// which keep turning slowly so work in progress stays visible.
    /// </summary>
    private static async Task AssertNoMotionAsync(IPage page, string screen)
    {
        string[] moving = await page.EvaluateAsync<string[]>(
            """
            () => {
              const describe = e => e.tagName.toLowerCase()
                + (e.id ? '#' + e.id : '')
                + (typeof e.className === 'string' && e.className ? '.' + e.className.trim().split(/\s+/).join('.') : '');
              const moving = [];
              const seconds = value => value.split(',').map(v => parseFloat(v) || 0);
              for (const element of document.querySelectorAll('*')) {
                for (const pseudo of [null, '::before', '::after']) {
                  const style = getComputedStyle(element, pseudo);
                  const transition = seconds(style.transitionDuration).some(s => s > 0);
                  const animation = style.animationName !== 'none'
                    && seconds(style.animationDuration).some(s => s > 0)
                    && !element.closest('.sk-spin');
                  if (transition || animation)
                    moving.push(`${describe(element)}${pseudo ?? ''}: transition ${style.transitionDuration}, animation ${style.animationName}`);
                }
              }
              return moving;
            }
            """);

        Assert.True(moving.Length == 0, $"Elements still move under reduced motion on {screen}:{Environment.NewLine}{string.Join(Environment.NewLine, moving)}");
    }

    private static bool IsPlayerScript(string url) =>
        url.Contains("hls", StringComparison.Ordinal)
        || url.Contains("/Features/Player/", StringComparison.Ordinal);

    private static long Gzipped(byte[] body)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(body);
        return buffer.Length;
    }
}
