using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

[Collection(MockAppCollection.Name)]
[Trait("Category", "E2E")]
public sealed class PlayerJourneyTests(MockAppFixture app)
{
    private const string Ready = "[data-sk='seek'][aria-disabled='false']";

    [Fact]
    public async Task Plays_a_finished_recording_with_basic_controls_and_seek()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;

        var hlsRequests = new List<string>();
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("/lib/hls.js/", StringComparison.Ordinal))
                hlsRequests.Add(request.Url);
        };
        Assert.Empty(hlsRequests); // nothing on the library page

        ILocator saved = page.Locator(LibraryHooks.SavedRow);
        string first = (await saved.Nth(0).GetAttributeAsync("data-recording-id"))!;
        await saved.Nth(0).ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{first}", StringComparison.Ordinal));
        await ExpandLibraryOnWatchPageAsync(page);
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 20_000 });

        Assert.Single(hlsRequests);
        Assert.True(await Video(page, "v => v.paused"), "no autoplay");
        Assert.True(await Video(page, "v => !v.controls"));

        await page.Locator("[data-sk='center']").ClickAsync();
        await page.WaitForFunctionAsync("document.querySelector('[data-sk=video]').currentTime > 0.5");
        Assert.Equal("Pause", await page.Locator("[data-sk='play']").GetAttributeAsync("aria-label"));
        await page.Locator("[data-sk='play']").ClickAsync();
        Assert.True(await Video(page, "v => v.paused"));

        await page.Locator("[data-sk='speed']").ClickAsync();
        await page.Locator("[data-sk='speed-menu'] [data-rate='1.5']").ClickAsync();
        Assert.Equal(1.5, await page.Locator("[data-sk='video']").EvaluateAsync<double>("v => v.playbackRate"));
        Assert.Equal("1.5×", await page.Locator("[data-sk='speed']").InnerTextAsync());

        ILocator seek = page.Locator("[data-sk='seek']");
        await seek.FocusAsync();
        await page.Keyboard.PressAsync("Home");
        await page.Keyboard.PressAsync("ArrowRight");
        await WaitForTimeAsync(page, 5);
        Assert.Contains("0:05 of", await seek.GetAttributeAsync("aria-valuetext"));

        LocatorBoundingBoxResult box = (await seek.BoundingBoxAsync())!;
        await page.Mouse.ClickAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
        double duration = await page.Locator("[data-sk='video']").EvaluateAsync<double>("v => v.duration");
        await WaitForTimeAsync(page, duration / 2, tolerance: 1);

        await page.Locator("[data-sk='volume']").FocusAsync();
        await page.Keyboard.PressAsync("ArrowLeft");
        Assert.Equal(0.95, await page.Locator("[data-sk='video']").EvaluateAsync<double>("v => v.volume"), 2);

        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "player");

        // Another recording swaps the source in the same player element.
        await page.Locator("[data-sk='video']").EvaluateAsync("v => { v.__skMarker = true; }");
        string second = (await saved.Nth(1).GetAttributeAsync("data-recording-id"))!;
        string secondTitle = (await saved.Nth(1).Locator("[data-row-title]").InnerTextAsync()).Trim();
        await saved.Nth(1).ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{second}", StringComparison.Ordinal));
        await page.WaitForFunctionAsync(
            "t => document.querySelector('[data-sk=title]').textContent === t", secondTitle);
        Assert.True(await Video(page, "v => v.__skMarker === true"));
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 20_000 });
        Assert.True(await Video(page, "v => v.paused"));
        Assert.Single(hlsRequests);
    }

    [Fact]
    public async Task Clicking_the_video_frame_toggles_playback()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator saved = page.Locator(LibraryHooks.SavedRow).First;
        string id = (await saved.GetAttributeAsync("data-recording-id"))!;
        await saved.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 20_000 });

        await page.Locator("[data-sk='center']").ClickAsync();
        await page.WaitForFunctionAsync("!document.querySelector('[data-sk=video]').paused");

        ILocator video = page.Locator("[data-sk='video']");
        LocatorBoundingBoxResult box = (await video.BoundingBoxAsync())!;
        await page.Mouse.ClickAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
        Assert.True(await Video(page, "v => v.paused"), "a frame click pauses playback");

        // Paused, the centre play surface covers the frame and resumes.
        await page.Locator("[data-sk='center']").ClickAsync();
        await page.WaitForFunctionAsync("!document.querySelector('[data-sk=video]').paused");
        await page.Mouse.ClickAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
        Assert.True(await Video(page, "v => v.paused"), "a second frame click pauses again");
    }

    [Fact]
    public async Task Touch_tap_reveals_controls_without_toggling_playback()
    {
        await using Session session = await OpenAsync(app.BaseUrl, hasTouch: true);
        IPage page = session.Page;
        ILocator saved = page.Locator(LibraryHooks.SavedRow).First;
        string id = (await saved.GetAttributeAsync("data-recording-id"))!;
        await saved.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 20_000 });

        await page.Locator("[data-sk='center']").TapAsync();
        await page.WaitForFunctionAsync("!document.querySelector('[data-sk=video]').paused");

        LocatorBoundingBoxResult box = (await page.Locator("[data-sk='video']").BoundingBoxAsync())!;
        await page.Touchscreen.TapAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
        await page.WaitForTimeoutAsync(500);

        Assert.False(await Video(page, "v => v.paused"), "a touch tap must not toggle playback");
    }

    [Fact]
    public async Task A_failing_playlist_retries_then_offers_retry_that_recovers()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator saved = page.Locator(LibraryHooks.SavedRow);
        string id = (await saved.First.GetAttributeAsync("data-recording-id"))!;

        int failures = 0;
        await page.RouteAsync("**/media/**/index.m3u8", async route =>
        {
            failures++;
            await route.FulfillAsync(new() { Status = 404, Body = "missing" });
        });

        await saved.First.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));

        ILocator overlay = page.Locator("[data-sk='overlay']");
        await overlay.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        Assert.True(failures >= 4, $"expected the first load plus three retries, saw {failures}");
        Assert.Equal("alert", await overlay.GetAttributeAsync("role"));
        Assert.True(await page.Locator("[data-sk='retry']").EvaluateAsync<bool>("b => b === document.activeElement"));
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "player-error");

        await page.UnrouteAllAsync();
        await page.Locator("[data-sk='retry']").ClickAsync();
        await overlay.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 20_000 });
    }

    [Fact]
    public async Task Stats_shows_the_viewer_tile_and_chart_and_the_chart_seeks()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        string href = (await page.Locator(LibraryHooks.SavedRow).First.GetAttributeAsync("href"))!;
        await page.GotoAsync(new Uri(new Uri(page.Url), href).ToString(),
            new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 30_000 });
        string id = page.Url[(page.Url.LastIndexOf('/') + 1)..];

        // The seeded samples are the independent source of truth for the tile figures.
        string viewersJson = await page.EvaluateAsync<string>(
            "async id => JSON.stringify(await (await fetch(`/api/recordings/${id}/viewers`)).json())", id);
        using JsonDocument viewers = JsonDocument.Parse(viewersJson);
        JsonElement[] samples = [.. viewers.RootElement.GetProperty("samples").EnumerateArray()];
        Assert.True(samples.Length >= 2, $"The Mock seeder writes viewer samples for {id}: {viewersJson}");
        int now = samples[^1].GetProperty("count").GetInt32();
        double domain = Math.Max(
            Math.Max(viewers.RootElement.GetProperty("durationSec").GetDouble(), samples[^1].GetProperty("at").GetDouble()),
            1);

        await page.Locator("[data-sk='stats']").ClickAsync();
        ILocator panel = page.Locator("[data-sk-stats]:not([data-chrome='tab'])");
        await Assertions.Expect(panel).ToHaveAttributeAsync("data-open", "true");

        ILocator tile = panel.Locator("[data-sk='viewer-tile']");
        await Assertions.Expect(tile).ToBeVisibleAsync(new() { Timeout = 10_000 });
        await Assertions.Expect(panel.Locator("[data-sk='viewer-chart-empty']")).ToBeHiddenAsync();
        await Assertions.Expect(tile.Locator("[data-sk='viewer-now']"))
            .ToHaveTextAsync(now.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        await Assertions.Expect(tile.Locator("[data-sk='viewer-peak']")).ToHaveTextAsync(new Regex(@"^[\d,]+ at \d+:\d\d$"));
        await Assertions.Expect(tile.Locator("[data-sk='viewer-average']")).ToHaveTextAsync(new Regex(@"^[\d,]+$"));

        ILocator chart = panel.GetByRole(AriaRole.Img, new() { NameRegex = new Regex("^Viewer count over the recording: ") });
        await Assertions.Expect(chart).ToHaveAttributeAsync("data-sk", "viewer-chart");
        await Assertions.Expect(chart.Locator("[data-series='line']")).ToHaveCountAsync(1);
        await Assertions.Expect(chart.Locator("[data-axis='x']").First).ToHaveTextAsync("0:00");
        await Assertions.Expect(panel.Locator("[data-sk='rows-viewers'] tr")).ToHaveCountAsync(samples.Length);
        // The sample table is exposed to screen readers but paints nothing (see the stats journey).
        Assert.True(await panel.Locator("[data-sk='table-viewers']").EvaluateAsync<bool>(
            """
            t => {
              const box = t.getBoundingClientRect();
              const style = getComputedStyle(t);
              return style.display !== 'none' && style.visibility !== 'hidden'
                && ((box.width <= 1 && box.height <= 1) || /^inset\(50%/.test(style.clipPath));
            }
            """));

        // Hover shows the time and count of the nearest sample.
        await chart.HoverAsync();
        await Assertions.Expect(panel.Locator("[data-sk='tip-viewers']"))
            .ToHaveTextAsync(new Regex(@"^\d+:\d\d · [\d,]+ viewers?$"));

        // A click seeks the player to that recording time, and the tile follows the playhead.
        LocatorBoundingBoxResult box = (await chart.BoundingBoxAsync())!;
        double target = domain * 0.75;
        await page.Mouse.ClickAsync(box.X + (box.Width * 0.75f), box.Y + (box.Height / 2));
        await page.WaitForFunctionAsync(
            "([t, tol]) => Math.abs(document.querySelector('[data-sk=video]').currentTime - t) <= tol",
            new object[] { target, 2.0 },
            new PageWaitForFunctionOptions { Timeout = 10_000 });
        double position = await page.Locator("[data-sk='video']").EvaluateAsync<double>("v => v.currentTime");
        await Assertions.Expect(tile.Locator("[data-sk='viewer-at']"))
            .ToHaveTextAsync($"Viewers at {Timecode(position)}");

        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "player-viewers");

        // On a phone the Stats tab holds the panel; the tile stacks above the full-width chart.
        await using Session phone = await OpenAsync(app.BaseUrl, viewport: new() { Width = 375, Height = 812 });
        page = phone.Page;
        await page.GotoAsync(new Uri(new Uri(page.Url), href).ToString(),
            new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 30_000 });
        await page.GetByRole(AriaRole.Tab, new() { Name = "Stats" }).ClickAsync();
        ILocator tab = page.Locator("[data-sk-stats][data-chrome='tab']");
        await Assertions.Expect(tab.Locator("[data-sk='viewer-tile']")).ToBeVisibleAsync(new() { Timeout = 10_000 });
        await Assertions.Expect(tab.GetByRole(AriaRole.Img, new() { NameRegex = new Regex("^Viewer count over the recording: ") }))
            .ToBeVisibleAsync();
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "player-viewers-phone");
    }

    // Mirrors Ui/Format.Timecode and /js/timecode.js.
    private static string Timecode(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Floor(Math.Max(0, seconds)));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    [Fact]
    public async Task Stats_open_with_the_player_button_samples_and_copies_a_versioned_snapshot()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        await session.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);

        ILocator growing = page.Locator(
            LibraryHooks.Row,
            new() { HasTextString = "Community day" });
        string id = (await growing.GetAttributeAsync("data-recording-id"))!;
        await growing.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 20_000 });

        ILocator panel = page.Locator("[data-sk-stats]");
        ILocator body = page.Locator("[data-sk='stats-body']");
        ILocator playerButton = page.Locator("[data-sk='stats']");
        ILocator toggle = page.Locator("[data-sk='stats-toggle']");

        // Closed is one header row and an inert body.
        Assert.Equal("false", await panel.GetAttributeAsync("data-open"));
        Assert.Equal("false", await playerButton.GetAttributeAsync("aria-pressed"));
        Assert.True(await body.EvaluateAsync<bool>("b => b.inert"));
        double collapsedHeight = (await panel.BoundingBoxAsync())!.Height;
        Assert.InRange(collapsedHeight, 47, 49);

        await playerButton.ClickAsync();
        await page.WaitForFunctionAsync(
            """() => document.querySelector("[data-sk-stats]").dataset.open === 'true'""",
            null,
            new PageWaitForFunctionOptions { Timeout = 5_000 });
        Assert.Equal("true", await panel.GetAttributeAsync("data-open"));
        Assert.Equal("true", await playerButton.GetAttributeAsync("aria-pressed"));
        Assert.Equal("true", await toggle.GetAttributeAsync("aria-checked"));
        Assert.False(await body.EvaluateAsync<bool>("b => b.inert"));

        // The heading and the data tables are for screen readers only: rendered and exposed, but
        // painting no visible area, so the data-sr-only hook cannot drift from the CSS. A table
        // keeps its content width despite width: 1px, so a clip-path that insets half of each
        // side (nothing left to paint) also counts as hidden.
        // The viewer table is hidden until samples arrive (#211); the viewer journey checks it.
        string screenReaderOnlyJson = await panel.Locator("[data-sr-only]:not([data-sk='table-viewers'])").EvaluateAllAsync<string>(
            """
            elements => JSON.stringify(elements.map(e => {
              const box = e.getBoundingClientRect();
              const style = getComputedStyle(e);
              const clippedAway = /^inset\(50%/.test(style.clipPath);
              const tiny = box.width <= 1 && box.height <= 1;
              return { tag: e.tagName, width: box.width, height: box.height, clipPath: style.clipPath,
                       hidden: tiny || clippedAway, display: style.display, visibility: style.visibility };
            }))
            """);
        using JsonDocument screenReaderOnly = JsonDocument.Parse(screenReaderOnlyJson);
        Assert.Equal(5, screenReaderOnly.RootElement.GetArrayLength());
        Assert.All(screenReaderOnly.RootElement.EnumerateArray(), element =>
        {
            string tag = element.GetProperty("tag").GetString()!;
            Assert.True(element.GetProperty("hidden").GetBoolean(), $"{tag} paints no visible area: {element}");
            Assert.NotEqual("none", element.GetProperty("display").GetString());
            Assert.NotEqual("hidden", element.GetProperty("visibility").GetString());
        });

        // Play so the requestVideoFrameCallback frame-rate estimate has frames to count.
        await page.Locator("[data-sk='center']").ClickAsync();

        await page.WaitForFunctionAsync(
            """() => document.querySelectorAll("[data-sk='rows-buffer'] tr").length >= 3""",
            null,
            new PageWaitForFunctionOptions { Timeout = 20_000 });

        Assert.Equal("hls.js · source", (await page.Locator("[data-sk='stats-hint']").InnerTextAsync()).Trim());
        Assert.Equal("MSE · hls.js", (await page.Locator("[data-sk='kv-decoder']").InnerTextAsync()).Trim());
        Assert.Contains("index.m3u8", await page.Locator("[data-sk='kv-playlist']").GetAttributeAsync("title"));
        string bitrate = (await page.Locator("[data-sk='value-bitrate']").InnerTextAsync()).Trim();
        string frameRate = (await page.Locator("[data-sk='value-frame']").InnerTextAsync()).Trim();
        string bandwidth = (await page.Locator("[data-sk='value-bandwidth']").InnerTextAsync()).Trim();
        string buffer = (await page.Locator("[data-sk='value-buffer']").InnerTextAsync()).Trim();
        string lastSegment = (await page.Locator("[data-sk='kv-last-segment']").InnerTextAsync()).Trim();
        string segments = (await page.Locator("[data-sk='kv-segments']").InnerTextAsync()).Trim();
        Assert.True(bitrate != "—",
            $"bitrate={bitrate} frame={frameRate} bandwidth={bandwidth} buffer={buffer} " +
            $"last={lastSegment} segments={segments}");
        Assert.NotEqual("—", frameRate);
        Assert.NotEqual("—", bandwidth);
        Assert.NotEqual("—", buffer);
        Assert.Contains(" ms", lastSegment);
        Assert.NotEqual("—", segments);
        Assert.Equal("None", (await page.Locator("[data-sk='kv-errors']").InnerTextAsync()).Trim());

        ILocator bufferSpark = page.Locator("[data-sk='spark-buffer']");
        ILocator line = bufferSpark.Locator("polyline");
        await line.WaitForAsync();
        Assert.Equal("2px", await line.EvaluateAsync<string>("el => getComputedStyle(el).strokeWidth"));
        Assert.Equal(
            "var(--primary)",
            await page.Locator("[data-sk='spark-bitrate'] polyline").GetAttributeAsync("stroke"));
        // Network series are secondary (StatsPanel card; tertiary is the stream, #180).
        Assert.Equal("var(--secondary)", await line.GetAttributeAsync("stroke"));

        // The stats panel sits below the details card; hover scrolls it into view first.
        await bufferSpark.HoverAsync();
        Assert.Contains("s ago ·", await page.Locator("[data-sk='tip-buffer']").InnerTextAsync());
        Assert.Equal(
            "visible",
            await bufferSpark.Locator("[data-crosshair]").GetAttributeAsync("visibility"));

        // The active tooltip survives the next 2 s sparkline redraw.
        int samplesBeforeHover = await page.Locator("[data-sk='rows-buffer'] tr").CountAsync();
        await page.WaitForFunctionAsync(
            """(count) => document.querySelectorAll("[data-sk='rows-buffer'] tr").length > count""",
            samplesBeforeHover,
            new PageWaitForFunctionOptions { Timeout = 5_000 });
        Assert.Contains("s ago ·", await page.Locator("[data-sk='tip-buffer']").InnerTextAsync());
        Assert.Equal(
            "visible",
            await bufferSpark.Locator("[data-crosshair]").GetAttributeAsync("visibility"));

        // The growing capture may or may not have its first viewer sample yet (#211): wait for
        // the Stream section to settle on either the empty line or the tile.
        await page.Locator(
            "[data-sk-stats] [data-sk='viewer-chart-empty']:not([hidden]), [data-sk-stats] [data-sk='viewer-tile']:not([hidden])")
            .First.WaitForAsync();
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "player-stats");

        await page.Locator("[data-sk='copy-stats']").ClickAsync();
        await Feedback.Snackbar(page, "Stats copied").WaitForAsync();

        await page.WaitForFunctionAsync(
            "async () => (await navigator.clipboard.readText()).length > 0",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });
        string json = await page.EvaluateAsync<string>("navigator.clipboard.readText()");
        using JsonDocument snapshot = JsonDocument.Parse(json);
        Assert.Equal(1, snapshot.RootElement.GetProperty("v").GetInt32());
        Assert.Equal(id, snapshot.RootElement.GetProperty("recordingId").GetString());
        Assert.Equal("live", snapshot.RootElement.GetProperty("mode").GetString());
        Assert.True(snapshot.RootElement.GetProperty("samples").GetArrayLength() >= 3);
        Assert.Equal(JsonValueKind.Array, snapshot.RootElement.GetProperty("errors").ValueKind);

        // Closing while focus is inside moves it to the switch, then stops the sampler.
        await page.Locator("[data-sk='copy-stats']").FocusAsync();
        await toggle.EvaluateAsync("el => el.click()");
        Assert.True(await toggle.EvaluateAsync<bool>("el => el === document.activeElement"));
        Assert.Equal("false", await panel.GetAttributeAsync("data-open"));
        Assert.Equal("false", await playerButton.GetAttributeAsync("aria-pressed"));
        Assert.True(await body.EvaluateAsync<bool>("b => b.inert"));
        await page.WaitForFunctionAsync(
            """() => document.querySelector("[data-sk-stats]").getBoundingClientRect().height <= 49""",
            null,
            new PageWaitForFunctionOptions { Timeout = 2_000 });
        Assert.InRange((await panel.BoundingBoxAsync())!.Height, 47, 49);

        // No new samples arrive after the panel closes.
        int rows = await page.Locator("[data-sk='rows-buffer'] tr").CountAsync();
        await page.WaitForFunctionAsync(
            """
            async (expected) => {
                await new Promise((resolve) => setTimeout(resolve, 2200));
                return document.querySelectorAll("[data-sk='rows-buffer'] tr").length === expected;
            }
            """,
            rows,
            new PageWaitForFunctionOptions { Timeout = 5_000 });

        // Reopening persists sk-ui, and a reload restores the panel open.
        await playerButton.ClickAsync();
        await page.WaitForFunctionAsync(
            """() => decodeURIComponent(document.cookie).includes('"stats":true')""",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });
        await page.ReloadAsync();
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 20_000 });
        Assert.Equal("true", await panel.GetAttributeAsync("data-open"));
        Assert.Equal("true", await playerButton.GetAttributeAsync("aria-pressed"));
        Assert.False(await body.EvaluateAsync<bool>("b => b.inert"));
    }

    [Fact]
    public async Task A_paused_position_resumes_after_reload_and_shows_a_resume_bar()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator row = page.Locator(LibraryHooks.SavedRow).First;
        string id = (await row.GetAttributeAsync("data-recording-id"))!;

        await row.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await ExpandLibraryOnWatchPageAsync(page);
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 20_000 });

        ILocator seek = page.Locator("[data-sk='seek']");
        await seek.FocusAsync();
        await page.Keyboard.PressAsync("Home");
        await page.Keyboard.PressAsync("ArrowRight");
        await page.Keyboard.PressAsync("ArrowRight");
        await WaitForTimeAsync(page, 10);
        await page.Locator("[data-sk='center']").ClickAsync();
        await page.WaitForFunctionAsync("document.querySelector('[data-sk=video]').currentTime > 10.2");
        await page.Locator("[data-sk='play']").ClickAsync();
        double paused = await page.Locator("[data-sk='video']").EvaluateAsync<double>("v => v.currentTime");

        ILocator bar = page.Locator($"{LibraryHooks.Drawer} a[data-recording-id='{id}'] [data-progress] i");
        await bar.WaitForAsync(new() { Timeout = 10_000 });

        await page.ReloadAsync();
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 20_000 });
        await WaitForTimeAsync(page, paused, tolerance: 1);
        Assert.True(await Video(page, "v => v.paused"), "resume never autoplays");
        await bar.WaitForAsync();
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "player-resumed");
    }

    [Fact]
    public async Task A_position_left_behind_the_live_edge_resumes_after_reload_until_Live_edge_forgets_it()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator growing = page.Locator(LibraryHooks.Row, new() { HasTextString = "Community day" });
        string id = (await growing.GetAttributeAsync("data-recording-id"))!;

        await growing.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 30_000 });
        ILocator live = page.Locator("[data-sk='live']");
        await live.WaitForAsync(new() { State = WaitForSelectorState.Visible });

        // Leave the live edge: seek near the start, play a moment, then pause.
        ILocator seek = page.Locator("[data-sk='seek']");
        await seek.FocusAsync();
        await page.Keyboard.PressAsync("Home");
        await page.Keyboard.PressAsync("ArrowRight");
        await WaitForTimeAsync(page, 5);
        await page.Locator("[data-sk='video']").EvaluateAsync("v => { v.muted = true; return v.play(); }");
        await page.WaitForFunctionAsync("document.querySelector('[data-sk=video]').currentTime > 5.2");
        await page.Locator("[data-sk='play']").ClickAsync();
        double paused = await page.Locator("[data-sk='video']").EvaluateAsync<double>("v => v.currentTime");

        // The row swaps the full live bar for a partial resume bar.
        await page.WaitForFunctionAsync(
            "id => { const i = document.querySelector(`nav[aria-label=Library] a[data-recording-id='${id}'] [data-progress] i`); return i && i.style.width !== '100%'; }",
            id,
            new() { Timeout = 10_000 });

        await page.ReloadAsync();
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 30_000 });
        await live.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        await WaitForTimeAsync(page, paused, tolerance: 1);
        Assert.True(await Video(page, "v => v.paused"), "resume never autoplays");
        Assert.Null(await live.GetAttributeAsync("data-at-edge"));

        // Live edge forgets the position: the row returns to the full live bar and the next
        // open starts at the live edge again.
        await live.ClickAsync();
        Assert.NotNull(await live.GetAttributeAsync("data-at-edge"));
        await page.WaitForFunctionAsync(
            "id => document.querySelector(`nav[aria-label=Library] a[data-recording-id='${id}'] [data-progress] i`)?.style.width === '100%'",
            id,
            new() { Timeout = 10_000 });

        await page.ReloadAsync();
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 30_000 });
        await live.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        Assert.NotNull(await live.GetAttributeAsync("data-at-edge"));
    }

    [Fact]
    public async Task A_growing_capture_opens_near_the_live_edge_follows_and_hands_off_on_endlist()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator growing = page.Locator(LibraryHooks.Row, new() { HasTextString = "Community day" });
        string id = (await growing.GetAttributeAsync("data-recording-id"))!;

        await growing.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 30_000 });

        ILocator live = page.Locator("[data-sk='live']");
        await live.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        Assert.True(await page.Locator("[data-sk='edge']").IsVisibleAsync(), "the red live tick shows");
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "player-live");
        Assert.True(await Video(page, "v => v.paused"), "no autoplay at the live edge");
        // Opened at the sync position: well past the start, behind the seekable end.
        Assert.True(await Video(page, """
            v => v.seekable.length > 0
              && v.currentTime > 1
              && v.currentTime <= v.seekable.end(v.seekable.length - 1)
            """), "opens near the live edge");

        double before = await page.Locator("[data-sk='video']").EvaluateAsync<double>("v => v.duration");
        await page.WaitForFunctionAsync(
            "d => document.querySelector('[data-sk=video]').duration > d", before,
            new() { Timeout = 45_000 });

        ILocator seek = page.Locator("[data-sk='seek']");
        await seek.FocusAsync();
        await page.Keyboard.PressAsync("Home");
        await WaitForTimeAsync(page, 0);
        Assert.Null(await live.GetAttributeAsync("data-at-edge"));

        await live.ClickAsync();
        await page.WaitForFunctionAsync("""
            () => {
              const v = document.querySelector('[data-sk=video]');
              return v.seekable.length > 0 && v.seekable.end(v.seekable.length - 1) - v.currentTime < 15;
            }
            """);
        Assert.NotNull(await live.GetAttributeAsync("data-at-edge"));

        // ENDLIST arrives on the next playlist refresh: live becomes finished in place.
        double position = await page.Locator("[data-sk='video']").EvaluateAsync<double>("v => v.currentTime");
        await page.RouteAsync($"**/media/*/{id}/index.m3u8", async route =>
        {
            IAPIResponse response = await route.FetchAsync();
            string body = await response.TextAsync();
            await route.FulfillAsync(new()
            {
                Response = response,
                Body = body.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal)
                    ? body
                    : body.TrimEnd() + "\n#EXT-X-ENDLIST\n",
            });
        });
        await live.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 45_000 });
        Assert.False(await page.Locator("[data-sk='edge']").IsVisibleAsync());
        await WaitForTimeAsync(page, position, tolerance: 1);
    }

    [Fact]
    public async Task Chrome_idles_takes_shortcuts_toggles_theater_and_remembers_preferences()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator row = page.Locator(LibraryHooks.SavedRow).First;
        string id = (await row.GetAttributeAsync("data-recording-id"))!;
        await row.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 20_000 });
        await page.EvaluateAsync("() => document.activeElement?.blur()");

        // K plays from anywhere outside a field; overlays fade after 3 s without input.
        await page.Keyboard.PressAsync("k");
        await page.WaitForFunctionAsync("!document.querySelector('[data-sk=video]').paused");
        ILocator player = page.Locator("[data-sk-player]");
        await page.Mouse.MoveAsync(5, 5);
        await page.Locator("[data-sk-player][data-idle]").WaitForAsync(new() { Timeout = 6_000 });
        LocatorBoundingBoxResult box = (await player.BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(box.X + box.Width / 2, box.Y + box.Height / 3);
        await page.Locator("[data-sk-player]:not([data-idle])").WaitForAsync();

        await page.Keyboard.PressAsync("k");
        Assert.True(await Video(page, "v => v.paused"));
        await page.WaitForTimeoutAsync(3_500);
        Assert.False(await player.EvaluateAsync<bool>("p => p.hasAttribute('data-idle')"), "never idle while paused");

        // Typing in search is never a shortcut.
        await page.Locator("[data-search-input]").FillAsync("m");
        Assert.False(await Video(page, "v => v.muted"));
        await page.Locator("[data-search-input]").FillAsync("");
        await page.EvaluateAsync("() => document.activeElement?.blur()");

        await page.Keyboard.PressAsync("m");
        Assert.True(await Video(page, "v => v.muted"));
        await page.Locator("[data-sk='speed']").ClickAsync();
        await page.Locator("[data-sk='speed-menu'] [data-rate='1.5']").ClickAsync();
        await page.EvaluateAsync("() => document.activeElement?.blur()");

        await page.Keyboard.PressAsync("t");
        await page.Locator("[data-shell][data-theater-mode]").WaitForAsync();
        Assert.False(await page.Locator(LibraryHooks.Drawer).IsVisibleAsync());
        Assert.Equal("true", await page.Locator("[data-sk='theater']").GetAttributeAsync("aria-pressed"));
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "player-theater");
        await page.Locator("[data-sk='theater']").ClickAsync();
        await page.Locator("[data-shell]:not([data-theater-mode])").WaitForAsync();

        Assert.True(await page.Locator("[data-sk='fullscreen']").IsVisibleAsync());
        bool pipEnabled = await page.EvaluateAsync<bool>("() => document.pictureInPictureEnabled === true");
        Assert.Equal(pipEnabled, await page.Locator("[data-sk='pip']").IsVisibleAsync());
        Assert.Equal("Source", (await page.Locator("[data-quality]").InnerTextAsync()).Trim());

        await page.ReloadAsync();
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 20_000 });
        Assert.True(await Video(page, "v => v.muted"), "mute survives a reload");
        Assert.Equal(1.5, await page.Locator("[data-sk='video']").EvaluateAsync<double>("v => v.playbackRate"));
        Assert.Equal("1.5×", await page.Locator("[data-sk='speed']").InnerTextAsync());

        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        Assert.Equal("0s", await page.Locator("[data-sk=bottom-bar]").EvaluateAsync<string>(
            "e => getComputedStyle(e).transitionDuration"));
    }

    private static Task<bool> Video(IPage page, string expression) =>
        page.Locator("[data-sk='video']").EvaluateAsync<bool>(expression);

    private static Task WaitForTimeAsync(IPage page, double seconds, double tolerance = 0.25) =>
        page.WaitForFunctionAsync(
            "([t, tol]) => Math.abs(document.querySelector('[data-sk=video]').currentTime - t) <= tol",
            new object[] { seconds, tolerance });
}
