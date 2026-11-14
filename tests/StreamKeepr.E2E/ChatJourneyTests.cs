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
}
