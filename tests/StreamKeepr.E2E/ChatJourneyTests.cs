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
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref chatRequests) <= before)
        {
            Assert.True(DateTime.UtcNow < deadline, "the pane polls while live");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Live_chat_scrolls_inside_the_player_height_and_keeps_the_footer_in_place()
    {
        await using LibraryJourneyTests.Session session =
            await LibraryJourneyTests.OpenAsync(app.BaseUrl);
        IPage page = session.Page;

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

        // Wait until the rendered rows are taller than the player, so the log must overflow.
        await page.WaitForFunctionAsync(
            """
            () => {
              const rows = [...document.querySelectorAll('[data-sk=chat-log] .sk-chat-row')];
              const player = document.querySelector('.sk-player').getBoundingClientRect();
              return rows.reduce((sum, row) => sum + row.getBoundingClientRect().height, 0) >
                player.height;
            }
            """,
            null,
            new() { Timeout = 45_000 });

        LiveLayout layout = await page.EvaluateAsync<LiveLayout>(
            """
            () => {
              const pane = document.querySelector('[data-sk-chat]').getBoundingClientRect();
              const player = document.querySelector('.sk-player').getBoundingClientRect();
              const log = document.querySelector('[data-sk=chat-log]');
              const footer = document.querySelector('.sk-chat-footer').getBoundingClientRect();
              const rows = log.querySelectorAll('.sk-chat-row');
              const last = rows[rows.length - 1].getBoundingClientRect();
              const logBox = log.getBoundingClientRect();
              return {
                paneHeight: pane.height,
                playerHeight: player.height,
                footerBottom: footer.bottom,
                playerBottom: player.bottom,
                scrollable: log.scrollHeight > log.clientHeight,
                lastRowBottom: last.bottom,
                logBottom: logBox.bottom,
              };
            }
            """);

        // The pane follows the player's height instead of growing with its content.
        Assert.True(layout.PaneHeight <= layout.PlayerHeight + 1,
            $"chat pane {layout.PaneHeight}px is taller than the player {layout.PlayerHeight}px");
        Assert.True(layout.FooterBottom <= layout.PlayerBottom + 1,
            $"sync footer bottom {layout.FooterBottom}px is below the player {layout.PlayerBottom}px");
        Assert.True(layout.Scrollable, "the chat log scrolls once it overflows");
        // Following the live edge keeps the newest row in view at the bottom of the log.
        Assert.True(layout.LastRowBottom <= layout.LogBottom + 1,
            $"newest row bottom {layout.LastRowBottom}px is below the log {layout.LogBottom}px");
    }

    [Fact]
    public async Task Short_chat_is_anchored_to_the_bottom_of_the_log()
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

        ILocator pane = page.Locator("[data-sk-chat]");
        if (await pane.GetAttributeAsync("data-open") != "true")
            await page.Locator("[data-sk='chat-toggle']").ClickAsync();

        ILocator seek = page.Locator("[data-sk='seek']");
        await seek.FocusAsync();
        await page.Keyboard.PressAsync("Home");
        for (int i = 0; i < 4; i++)
            await page.Keyboard.PressAsync("ArrowRight");
        await page.Locator("[data-sk='chat-log']").GetByText("Welcome to the Mock stream")
            .WaitForAsync(new() { Timeout = 20_000 });

        ShortLayout layout = await page.EvaluateAsync<ShortLayout>(
            """
            () => {
              const log = document.querySelector('[data-sk=chat-log]');
              const rows = log.querySelectorAll('.sk-chat-row');
              const style = getComputedStyle(log);
              const box = log.getBoundingClientRect();
              return {
                overflowing: log.scrollHeight > log.clientHeight,
                firstRowTop: rows[0].getBoundingClientRect().top,
                lastRowBottom: rows[rows.length - 1].getBoundingClientRect().bottom,
                contentTop: box.top + parseFloat(style.paddingTop),
                contentBottom: box.bottom - parseFloat(style.paddingBottom),
              };
            }
            """);

        Assert.False(layout.Overflowing, "the seeded window should be shorter than the log");
        // Chat grows upward from the footer, like a live chat, instead of hanging from the top.
        Assert.True(Math.Abs(layout.ContentBottom - layout.LastRowBottom) <= 1,
            $"newest row ends at {layout.LastRowBottom}px, log content ends at {layout.ContentBottom}px");
        Assert.True(layout.FirstRowTop > layout.ContentTop + 1,
            "free space sits above the first row");
    }

    // Playwright deserializes evaluate results through a parameterless constructor.
    private sealed class LiveLayout
    {
        public double PaneHeight { get; set; }
        public double PlayerHeight { get; set; }
        public double FooterBottom { get; set; }
        public double PlayerBottom { get; set; }
        public bool Scrollable { get; set; }
        public double LastRowBottom { get; set; }
        public double LogBottom { get; set; }
    }

    private sealed class ShortLayout
    {
        public bool Overflowing { get; set; }
        public double FirstRowTop { get; set; }
        public double LastRowBottom { get; set; }
        public double ContentTop { get; set; }
        public double ContentBottom { get; set; }
    }
}
