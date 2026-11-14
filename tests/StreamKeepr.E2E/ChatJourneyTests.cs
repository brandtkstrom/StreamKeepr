using Microsoft.Playwright;

namespace StreamKeepr.E2E;

[Collection(MockAppCollection.Name)]
[Trait("Category", "E2E")]
public sealed class ChatJourneyTests(MockAppFixture app)
{
    [Fact]
    public async Task Opens_the_chat_pane_seeks_and_persists_the_open_state()
    {
        await using LibraryJourneyTests.Session session =
            await LibraryJourneyTests.OpenAsync(app.BaseUrl);
        IPage page = session.Page;

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

        await page.Locator("[data-sk='chat-log']").GetByText("Welcome to the Mock stream")
            .WaitForAsync(new() { Timeout = 20_000 });

        await page.ReloadAsync();
        await page.Locator("[data-sk='seek'][aria-disabled='false']")
            .WaitForAsync(new() { Timeout = 20_000 });
        Assert.Equal("true", await page.Locator("[data-sk-chat]").GetAttributeAsync("data-open"));
        Assert.Equal("true", await page.Locator("[data-sk='chat-toggle']")
            .GetAttributeAsync("aria-expanded"));
    }

    [Fact]
    public async Task Polls_live_chat_while_a_growing_capture_plays()
    {
        await using LibraryJourneyTests.Session session =
            await LibraryJourneyTests.OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        int chatRequests = 0;
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("/chat?", StringComparison.Ordinal))
                Interlocked.Increment(ref chatRequests);
        };

        ILocator growing = page.Locator(
            "nav#sk-drawer a.sk-row", new() { HasTextString = "Community day" });
        string id = (await growing.GetAttributeAsync("data-recording-id"))!;
        await growing.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await page.Locator("[data-sk='live']").WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        ILocator pane = page.Locator("[data-sk-chat]");
        if (await pane.GetAttributeAsync("data-open") != "true")
            await page.Locator("[data-sk='chat-toggle']").ClickAsync();
        await page.Locator("[data-sk='live']").ClickAsync();
        await page.Locator("[data-sk='video']").EvaluateAsync(
            "v => { v.muted = true; return v.play(); }");

        // The Mock source writes a message every 0.5 s near the live edge.
        await page.Locator("[data-sk='chat-log'] .sk-chat-row").First
            .WaitForAsync(new() { Timeout = 30_000 });
        await page.WaitForFunctionAsync(
            "() => document.querySelector('[data-sk=chat-state]').dataset.state === 'live'");
        int before = Volatile.Read(ref chatRequests);
        await page.WaitForTimeoutAsync(3_000);

        Assert.True(Volatile.Read(ref chatRequests) > before, "the pane polls while live");
    }
}
