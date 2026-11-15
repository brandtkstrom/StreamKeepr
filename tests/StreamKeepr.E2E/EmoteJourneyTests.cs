using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace StreamKeepr.E2E;

[Collection(MockAppCollection.Name)]
[Trait("Category", "E2E")]
public sealed class EmoteJourneyTests(MockAppFixture app)
{
    // A 1x1 transparent PNG served for every fixture image; it only needs to decode.
    private static readonly byte[] Pixel = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private const string ImageHost = "https://emotes.mock.invalid/";

    /// <summary>
    /// The Mock profile's fixed catalog is served without BTTV, and the browser images are
    /// intercepted so no test run can reach the real CDN. One catalog code points at a missing
    /// image to exercise the text fallback.
    /// </summary>
    [Fact]
    public async Task Renders_a_third_party_emote_and_falls_back_to_text_when_its_image_fails()
    {
        await using LibraryJourneyTests.Session session =
            await LibraryJourneyTests.OpenAsync(app.BaseUrl);
        IPage page = session.Page;

        var betterTvRequests = new ConcurrentQueue<string>();
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("betterttv.net", StringComparison.OrdinalIgnoreCase))
                betterTvRequests.Enqueue(request.Url);
        };

        await page.RouteAsync($"{ImageHost}**", async route =>
        {
            if (route.Request.Url.Contains("/000000000000000000000001/", StringComparison.Ordinal))
            {
                await route.FulfillAsync(new() { Status = 404, Body = "missing" });
                return;
            }

            await route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "image/png",
                BodyBytes = Pixel,
            });
        });

        ILocator saved = page.Locator("nav#sk-drawer a.sk-row[data-state='saved']").First;
        string id = (await saved.GetAttributeAsync("data-recording-id"))!;
        await saved.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await page.Locator("[data-sk='seek'][aria-disabled='false']")
            .WaitForAsync(new() { Timeout = 20_000 });

        await page.Locator("[data-sk='chat-toggle']").ClickAsync();
        await page.WaitForFunctionAsync(
            "() => document.querySelector('[data-sk-chat]').dataset.open === 'true'");

        ILocator seek = page.Locator("[data-sk='seek']");
        await seek.FocusAsync();
        await page.Keyboard.PressAsync("Home");
        for (int i = 0; i < 4; i++)
            await page.Keyboard.PressAsync("ArrowRight");

        ILocator row = page.Locator("[data-sk='chat-log'] .sk-chat-row")
            .Filter(new() { HasTextString = "Fixture emotes" });
        await row.WaitForAsync(new() { Timeout = 20_000 });

        ILocator kekw = row.Locator("img[alt='KEKW']");
        await kekw.WaitForAsync(new() { Timeout = 10_000 });
        await page.WaitForFunctionAsync(
            "img => img.complete && img.naturalWidth > 0",
            await kekw.ElementHandleAsync());
        Assert.Equal(
            $"{ImageHost}emote/5ff36d0a95e8e11f4f6f0a1c/1x.webp",
            await kekw.GetAttributeAsync("src"));
        Assert.Contains(
            $"{ImageHost}emote/5ff36d0a95e8e11f4f6f0a1c/2x.webp 2x",
            await kekw.GetAttributeAsync("srcset"),
            StringComparison.Ordinal);
        Assert.Equal("lazy", await kekw.GetAttributeAsync("loading"));
        Assert.Equal("async", await kekw.GetAttributeAsync("decoding"));
        Assert.Equal("no-referrer", await kekw.GetAttributeAsync("referrerpolicy"));

        ILocator punctuation = row.Locator("img[alt=':tf:']");
        await punctuation.WaitForAsync(new() { Timeout = 10_000 });
        Assert.Equal(
            $"{ImageHost}emote/54fa8f1401e468494b85b537/1x.webp",
            await punctuation.GetAttributeAsync("src"));

        ILocator animated = row.Locator("img[alt='CatJAM']");
        await animated.WaitForAsync(new() { Timeout = 10_000 });
        await page.WaitForFunctionAsync(
            "img => img.complete && img.naturalWidth > 0",
            await animated.ElementHandleAsync());
        Assert.Equal(
            $"{ImageHost}emote/566ca38765dbbdab32ec0560/static/1x.webp",
            await animated.GetAttributeAsync("src"));

        await page.WaitForFunctionAsync(
            "row => !row.querySelector(\"img[alt='BrokenEmote']\") && " +
            "row.textContent.includes('BrokenEmote')",
            await row.ElementHandleAsync());
        Assert.Equal(0, await row.Locator("img[alt='BrokenEmote']").CountAsync());

        Assert.Empty(betterTvRequests);
    }
}
