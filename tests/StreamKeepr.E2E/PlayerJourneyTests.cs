using System.Text.Json;
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

        ILocator saved = page.Locator("nav#sk-drawer a.sk-row[data-state='saved']");
        string first = (await saved.Nth(0).GetAttributeAsync("data-recording-id"))!;
        await saved.Nth(0).ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{first}", StringComparison.Ordinal));
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
        string secondTitle = (await saved.Nth(1).Locator(".sk-row-title").InnerTextAsync()).Trim();
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
    public async Task A_failing_playlist_retries_then_offers_retry_that_recovers()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator saved = page.Locator("nav#sk-drawer a.sk-row[data-state='saved']");
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
    public async Task Stats_open_with_the_player_button_samples_and_copies_a_versioned_snapshot()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        await session.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);

        ILocator growing = page.Locator(
            "nav#sk-drawer a.sk-row",
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
        ILocator line = bufferSpark.Locator("polyline.line");
        await line.WaitForAsync();
        Assert.Equal("2px", await line.EvaluateAsync<string>("el => getComputedStyle(el).strokeWidth"));
        Assert.Equal(
            "var(--primary)",
            await page.Locator("[data-sk='spark-bitrate'] polyline.line").GetAttributeAsync("stroke"));
        Assert.Equal("var(--tertiary)", await line.GetAttributeAsync("stroke"));

        LocatorBoundingBoxResult sparkBox = (await bufferSpark.BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(sparkBox.X + sparkBox.Width / 2, sparkBox.Y + sparkBox.Height / 2);
        Assert.Contains("s ago ·", await page.Locator("[data-sk='tip-buffer']").InnerTextAsync());
        Assert.Equal(
            "visible",
            await bufferSpark.Locator("line.xh").GetAttributeAsync("visibility"));

        // The active tooltip survives the next 2 s sparkline redraw.
        int samplesBeforeHover = await page.Locator("[data-sk='rows-buffer'] tr").CountAsync();
        await page.WaitForFunctionAsync(
            """(count) => document.querySelectorAll("[data-sk='rows-buffer'] tr").length > count""",
            samplesBeforeHover,
            new PageWaitForFunctionOptions { Timeout = 5_000 });
        Assert.Contains("s ago ·", await page.Locator("[data-sk='tip-buffer']").InnerTextAsync());
        Assert.Equal(
            "visible",
            await bufferSpark.Locator("line.xh").GetAttributeAsync("visibility"));

        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "player-stats");

        await page.Locator("[data-sk='copy-stats']").ClickAsync();
        await page.Locator(".sk-snackbar").WaitForAsync();
        Assert.Contains("Stats copied", await page.Locator(".sk-snackbar").InnerTextAsync());

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
        ILocator row = page.Locator("nav#sk-drawer a.sk-row[data-state='saved']").First;
        string id = (await row.GetAttributeAsync("data-recording-id"))!;

        await row.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
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

        ILocator bar = page.Locator($"nav#sk-drawer a.sk-row[data-recording-id='{id}'] .prog i");
        await bar.WaitForAsync(new() { Timeout = 10_000 });

        await page.ReloadAsync();
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 20_000 });
        await WaitForTimeAsync(page, paused, tolerance: 1);
        Assert.True(await Video(page, "v => v.paused"), "resume never autoplays");
        await bar.WaitForAsync();
    }

    [Fact]
    public async Task A_growing_capture_opens_near_the_live_edge_follows_and_hands_off_on_endlist()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator growing = page.Locator("nav#sk-drawer a.sk-row", new() { HasTextString = "Community day" });
        string id = (await growing.GetAttributeAsync("data-recording-id"))!;

        await growing.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await page.Locator(Ready).WaitForAsync(new() { Timeout = 30_000 });

        ILocator live = page.Locator("[data-sk='live']");
        await live.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        Assert.True(await page.Locator("[data-sk='edge']").IsVisibleAsync(), "the red live tick shows");
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
        Assert.DoesNotContain("at-edge", await live.GetAttributeAsync("class") ?? "");

        await live.ClickAsync();
        await page.WaitForFunctionAsync("""
            () => {
              const v = document.querySelector('[data-sk=video]');
              return v.seekable.length > 0 && v.seekable.end(v.seekable.length - 1) - v.currentTime < 15;
            }
            """);
        Assert.Contains("at-edge", await live.GetAttributeAsync("class") ?? "");

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

    private static Task<bool> Video(IPage page, string expression) =>
        page.Locator("[data-sk='video']").EvaluateAsync<bool>(expression);

    private static Task WaitForTimeAsync(IPage page, double seconds, double tolerance = 0.25) =>
        page.WaitForFunctionAsync(
            "([t, tol]) => Math.abs(document.querySelector('[data-sk=video]').currentTime - t) <= tol",
            new object[] { seconds, tolerance });
}
