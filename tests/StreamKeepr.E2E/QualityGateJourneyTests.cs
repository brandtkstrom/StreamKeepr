using System.Collections.Concurrent;
using System.IO.Compression;
using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

/// <summary>
/// Release gates from the testing contract §8: the first-paint JS budget, reduced motion, and
/// keyboard-only reach.
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
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
        await using IBrowserContext context = await browser.NewContextAsync(
            new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1280, Height = 820 } });
        IPage page = await context.NewPageAsync();

        // Responses arrive on Playwright's event thread while the test reads the queue.
        var scripts = new ConcurrentQueue<IResponse>();
        page.Response += (_, response) =>
        {
            if (response.Request.ResourceType == "script")
                scripts.Enqueue(response);
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
        await Expect(() => Task.FromResult(scripts.Any(response => IsHlsScript(response.Url)) ? 1 : 0), 1);
        Assert.Contains(scripts, response => IsPlayerModule(response.Url));
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
    public async Task A_keyboard_only_journey_reaches_every_control_and_never_loses_focus()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        await page.Locator("nav#sk-drawer a.sk-row").First.WaitForAsync();
        await AssertTabReachesEveryControlAsync(page, "home");

        // Into the library tree by Tab; rows are one roving stop, moved with the arrow keys.
        await page.EvaluateAsync("() => document.activeElement?.blur()");
        await TabToAsync(page, "e => e.closest(\"[role='tree']\") !== null", "the library tree");
        await page.Keyboard.PressAsync("Home");
        for (int row = 0; row < 40 && !await FocusedMatchesAsync(page, "e => e.dataset.state === 'saved'"); row++)
            await page.Keyboard.PressAsync("ArrowDown");
        Assert.True(await FocusedMatchesAsync(page, "e => e.dataset.state === 'saved'"), "arrows reach a saved recording");
        await page.Keyboard.PressAsync("Enter");
        await page.Locator("[data-recording-details]").WaitForAsync();
        // Controls such as the seek bar and Go live appear only once the source has loaded.
        await page.Locator("[data-sk='seek'][aria-disabled='false']").WaitForAsync(new() { Timeout = 30_000 });
        await AssertTabReachesEveryControlAsync(page, "recording");

        // The speed menu opens from the keyboard, keeps focus in it, and Escape returns it.
        await TabToAsync(page, "e => e.dataset.sk === 'speed'", "the speed button");
        await page.Keyboard.PressAsync("Enter");
        await page.Locator("[data-sk='speed-menu']").WaitForAsync();
        Assert.True(await FocusedMatchesAsync(page, "e => e.closest(\"[data-sk='speed-menu']\") !== null"), "focus moves into the speed menu");
        await page.Keyboard.PressAsync("ArrowDown");
        Assert.True(await FocusedMatchesAsync(page, "e => e.closest(\"[data-sk='speed-menu']\") !== null"), "arrows stay in the menu");
        await page.Keyboard.PressAsync("Escape");
        Assert.True(await FocusedMatchesAsync(page, "e => e.dataset.sk === 'speed'"), "Escape returns focus to the speed button");

        // Stats open from the keyboard and their controls join the tab order.
        await TabToAsync(page, "e => e.dataset.sk === 'stats'", "the stats button");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("() => document.querySelector('[data-sk-stats]').dataset.open === 'true'");
        await AssertTabReachesEveryControlAsync(page, "recording with stats");
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "keyboard-stats");

        // The delete dialog traps focus while open and gives it back on Escape. Nothing is deleted.
        await TabToAsync(page, "e => e.hasAttribute('data-delete')", "the delete button");
        await page.Keyboard.PressAsync("Enter");
        await page.Locator("[data-delete-dialog][open]").WaitForAsync();
        await AssertFocusStaysInsideAsync(page, "[data-delete-dialog]", nativeModal: true);
        await page.Keyboard.PressAsync("Escape");
        await page.Locator("[data-delete-dialog][open]").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        Assert.True(await FocusedMatchesAsync(page, "e => e.hasAttribute('data-delete')"), "Escape returns focus to Delete");

        // Settings by keyboard from the top bar.
        await TabToAsync(page, "e => e.getAttribute('href') === '/settings'", "the Settings link");
        await page.Keyboard.PressAsync("Enter");
        await page.Locator("[data-channel]").First.WaitForAsync();
        await AssertTabReachesEveryControlAsync(page, "settings");
    }

    [Fact]
    public async Task On_a_phone_the_modal_drawer_traps_focus_is_axe_clean_and_holds_still_under_reduced_motion()
    {
        await using Session session = await OpenAsync(
            app.BaseUrl, viewport: new ViewportSize { Width = 375, Height = 812 });
        IPage page = session.Page;
        await page.Locator("[data-drawer-toggle]").WaitForAsync();
        await AssertTabReachesEveryControlAsync(page, "phone home");

        await page.EmulateMediaAsync(new PageEmulateMediaOptions { ReducedMotion = ReducedMotion.Reduce });
        await TabToAsync(page, "e => e.hasAttribute('data-drawer-toggle')", "the drawer button");
        await page.Keyboard.PressAsync("Enter");
        await page.Locator(".sk-shell.drawer-modal-open").WaitForAsync();
        Assert.True(await FocusedMatchesAsync(page, "e => e.closest('nav#sk-drawer') !== null"), "focus moves into the drawer");
        await AssertFocusStaysInsideAsync(page, "nav#sk-drawer", nativeModal: false);
        await AssertNoMotionAsync(page, "phone drawer");
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "phone-drawer");

        await page.Keyboard.PressAsync("Escape");
        await page.Locator(".sk-shell:not(.drawer-modal-open)").WaitForAsync();
        Assert.True(await FocusedMatchesAsync(page, "e => e.hasAttribute('data-drawer-toggle')"), "Escape returns focus to the drawer button");
    }

    /// <summary>
    /// Presses Tab from the top of the page until focus comes round again. Fails if any
    /// visible, enabled control with a tab stop was never focused, or if focus ever landed on
    /// something invisible (a hidden focus trap). Roving items (<c>tabindex="-1"</c>) are
    /// reached with arrow keys and are not tab stops.
    /// </summary>
    private static async Task AssertTabReachesEveryControlAsync(IPage page, string screen)
    {
        await page.EvaluateAsync(
            "() => { document.querySelectorAll('[data-sk-reached]').forEach(e => e.removeAttribute('data-sk-reached')); document.activeElement?.blur(); }");

        int tabStops = 0;
        var hidden = new List<string>();
        for (int press = 0; press < 400; press++)
        {
            await page.Keyboard.PressAsync("Tab");
            string state = await page.EvaluateAsync<string>(
                """
                async () => {
                  const e = document.activeElement;
                  if (!e || e === document.body) return 'none';
                  if (e.hasAttribute('data-sk-reached')) return 'seen';
                  e.setAttribute('data-sk-reached', '');
                  // Controls such as the volume slider widen on focus; judge them once settled.
                  await Promise.all(e.getAnimations().map(a => a.finished.catch(() => {})));
                  const box = e.getBoundingClientRect();
                  return e.checkVisibility({ visibilityProperty: true }) && box.width > 0 && box.height > 0
                    ? 'new' : 'hidden:' + e.outerHTML.slice(0, 160);
                }
                """);
            if (state == "seen")
                break;
            if (state.StartsWith("hidden:", StringComparison.Ordinal))
                hidden.Add(state["hidden:".Length..]);
            if (state != "none")
                tabStops++;
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

        Assert.True(tabStops > 0, $"Tab never moved focus on {screen}");
        Assert.True(
            hidden.Count == 0,
            $"Tab focused invisible elements on {screen}:{Environment.NewLine}{string.Join(Environment.NewLine, hidden)}");
        Assert.True(
            unreached.Length == 0,
            $"Tab never reached these controls on {screen}:{Environment.NewLine}{string.Join(Environment.NewLine, unreached)}");
    }

    /// <summary>Presses Tab until the focused element matches, failing after a full lap.</summary>
    private static async Task TabToAsync(IPage page, string predicate, string what)
    {
        for (int press = 0; press < 200; press++)
        {
            await page.Keyboard.PressAsync("Tab");
            if (await FocusedMatchesAsync(page, predicate))
                return;
        }

        Assert.Fail($"Tab never reached {what}");
    }

    /// <summary>
    /// Tabs forward and back through an open dialog or drawer: focus never reaches page content
    /// outside it. A native modal dialog may hand focus to the browser's own UI between laps
    /// (the page then reports <c>body</c>), so <paramref name="nativeModal"/> allows that; the
    /// drawer's own trap wraps first ↔ last and must never let focus leave.
    /// </summary>
    private static async Task AssertFocusStaysInsideAsync(IPage page, string container, bool nativeModal)
    {
        foreach (string key in new[] { "Tab", "Tab", "Tab", "Tab", "Tab", "Shift+Tab", "Shift+Tab", "Shift+Tab" })
        {
            await page.Keyboard.PressAsync(key);
            string? escaped = await page.EvaluateAsync<string?>(
                "([c, native]) => { const e = document.activeElement; if (!e || e === document.body) return native ? null : 'the page body'; return e.closest(c) ? null : e.outerHTML.slice(0, 160); }",
                new object[] { container, nativeModal });
            Assert.True(escaped is null, $"{key} moved focus out of {container} to {escaped}");
        }

        Assert.True(
            await FocusedMatchesAsync(page, $"e => e.closest(\"{container}\") !== null")
                || await page.EvaluateAsync<bool>("() => document.activeElement === document.body"),
            $"focus is not in {container}");
    }

    private static Task<bool> FocusedMatchesAsync(IPage page, string predicate) =>
        page.EvaluateAsync<bool>($"() => {{ const e = document.activeElement; return !!e && e !== document.body && ({predicate})(e); }}");

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

    private static bool IsHlsScript(string url) => url.Contains("/lib/hls.js/", StringComparison.Ordinal);

    private static bool IsPlayerModule(string url) => url.Contains("/Features/Player/", StringComparison.Ordinal);

    private static bool IsPlayerScript(string url) => IsHlsScript(url) || IsPlayerModule(url);

    private static long Gzipped(byte[] body)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(body);
        return buffer.Length;
    }
}
