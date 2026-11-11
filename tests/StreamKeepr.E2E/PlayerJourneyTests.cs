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

    private static Task<bool> Video(IPage page, string expression) =>
        page.Locator("[data-sk='video']").EvaluateAsync<bool>(expression);

    private static Task WaitForTimeAsync(IPage page, double seconds, double tolerance = 0.25) =>
        page.WaitForFunctionAsync(
            "([t, tol]) => Math.abs(document.querySelector('[data-sk=video]').currentTime - t) <= tol",
            new object[] { seconds, tolerance });
}
