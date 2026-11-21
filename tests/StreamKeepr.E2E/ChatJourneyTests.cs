using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;
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

        ILocator saved = page.Locator(LibraryHooks.SavedRow).First;
        string id = (await saved.GetAttributeAsync("data-recording-id"))!;
        await saved.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await page.Locator("[data-sk='seek'][aria-disabled='false']")
            .WaitForAsync(new() { Timeout = 20_000 });

        await page.Locator("[data-sk-player] [data-sk='chat']").ClickAsync();
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
        Assert.Equal("true", await page.Locator("[data-sk-player] [data-sk='chat']")
            .GetAttributeAsync("aria-pressed"));
    }

    [Fact]
    public async Task The_player_chat_button_mirrors_C_and_the_streamer_speaks_in_the_channel_colour()
    {
        await using LibraryJourneyTests.Session session =
            await LibraryJourneyTests.OpenAsync(app.BaseUrl);
        IPage page = session.Page;

        ILocator saved = page.Locator(LibraryHooks.SavedRow).First;
        string id = (await saved.GetAttributeAsync("data-recording-id"))!;
        await saved.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await page.Locator("[data-sk='seek'][aria-disabled='false']")
            .WaitForAsync(new() { Timeout = 20_000 });

        ILocator pane = page.Locator("[data-sk-chat]");
        ILocator chat = page.Locator("[data-sk-player]").GetByRole(AriaRole.Button, new() { Name = "Chat", Exact = true });
        await chat.WaitForAsync();
        Assert.Equal("C", await chat.GetAttributeAsync("aria-keyshortcuts"));
        string opened = (await pane.GetAttributeAsync("data-open"))!;
        Assert.Equal(opened, await chat.GetAttributeAsync("aria-pressed"));

        // The player's button and the C key make the same request, and the button mirrors both.
        await chat.ClickAsync();
        string toggled = opened == "true" ? "false" : "true";
        await Assertions.Expect(pane).ToHaveAttributeAsync("data-open", toggled);
        Assert.Equal(toggled, await chat.GetAttributeAsync("aria-pressed"));
        await page.Keyboard.PressAsync("c");
        await Assertions.Expect(pane).ToHaveAttributeAsync("data-open", opened);
        Assert.Equal(opened, await chat.GetAttributeAsync("aria-pressed"));
        if (opened != "true")
            await chat.ClickAsync();
        await Assertions.Expect(pane).ToHaveAttributeAsync("data-open", "true");

        ILocator seek = page.Locator("[data-sk='seek']");
        await seek.FocusAsync();
        await page.Keyboard.PressAsync("Home");
        for (int i = 0; i < 4; i++)
            await page.Keyboard.PressAsync("ArrowRight");
        ILocator streamer = page.Locator("[data-sk='chat-log'] [data-message-id='seed-streamer'] [data-chat-name]");
        await streamer.WaitForAsync(new() { Timeout = 20_000 });

        // Chat gets its colour from .NET in the pane payload and the drawer from its avatar;
        // the streamer's name must show the same channel colour the drawer shows.
        ChannelColours colours = await page.EvaluateAsync<ChannelColours>(
            """
            id => {
              const name = document.querySelector("[data-message-id='seed-streamer'] [data-chat-name]");
              const avatar = document.querySelector(`a[data-recording-id="${id}"]`)
                .closest('[data-streamer-group]').querySelector('[data-channel-color]');
              return {
                chat: name.dataset.channelColor ?? '',
                drawer: avatar.dataset.channelColor,
                shown: getComputedStyle(name).color,
              };
            }
            """, id);
        Assert.Equal(colours.Drawer, colours.Chat);
        Assert.Equal(await TokenColourAsync(page, $"--channel-{colours.Drawer}"), colours.Shown);

        // Keyboard focus rings: player-accent inside the player, primary in the chat island.
        await page.Keyboard.PressAsync("Shift+Tab");
        await chat.FocusAsync();
        Assert.Equal(await TokenColourAsync(page, "--player-accent"), await RingAsync(chat));
        await page.Locator("[data-sk='chat-log']").FocusAsync();
        Assert.Equal(await TokenColourAsync(page, "--primary"), await RingAsync(page.Locator("[data-sk='chat-log']")));

        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "player-chat");
    }

    [Fact]
    public async Task Chat_sync_in_settings_applies_live_to_the_open_pane_and_persists()
    {
        await using LibraryJourneyTests.Session session =
            await LibraryJourneyTests.OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        var offsets = new ConcurrentQueue<string>();
        page.Request += (_, request) =>
        {
            if (!request.Url.Contains("/chat?", StringComparison.Ordinal))
                return;
            string? offset = HttpUtility.ParseQueryString(new Uri(request.Url).Query)["syncOffsetSec"];
            if (offset is not null)
                offsets.Enqueue(offset);
        };

        ILocator saved = page.Locator(LibraryHooks.SavedRow).First;
        string id = (await saved.GetAttributeAsync("data-recording-id"))!;
        await saved.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await page.Locator("[data-sk='seek'][aria-disabled='false']")
            .WaitForAsync(new() { Timeout = 20_000 });
        ILocator pane = page.Locator("[data-sk-chat]");
        if (await pane.GetAttributeAsync("data-open") != "true")
            await page.Locator("[data-sk-player] [data-sk='chat']").ClickAsync();
        await WaitForOffsetAsync(offsets, "0.000");
        // The pane holds no sync control of its own.
        Assert.Equal(0, await pane.GetByRole(AriaRole.Slider).CountAsync());

        // Settings opens over the recording; the pane stays behind it.
        await page.GetByRole(AriaRole.Banner)
            .GetByRole(AriaRole.Link, new() { Name = "Settings", Exact = true }).ClickAsync();
        ILocator sheet = page.GetByRole(AriaRole.Complementary, new() { Name = "Settings", Exact = true });
        ILocator slider = sheet.GetByRole(AriaRole.Slider, new() { Name = "Chat sync", Exact = true });
        ILocator readout = sheet.Locator("[data-chat-sync-value]");
        await Assertions.Expect(slider).ToBeVisibleAsync(new() { Timeout = 20_000 });

        ILocator video = page.Locator("[data-sk='video']");
        double playhead = await video.EvaluateAsync<double>("v => v.currentTime");
        await slider.FocusAsync();
        for (int i = 0; i < 3; i++)
            await page.Keyboard.PressAsync("ArrowRight");
        await Assertions.Expect(readout).ToHaveTextAsync("+1.5 s");
        await Assertions.Expect(slider).ToHaveAttributeAsync("aria-valuetext", "1.5 seconds later");
        // The open pane behind the sheet refetches with the new offset at once.
        await WaitForOffsetAsync(offsets, "1.500");

        for (int i = 0; i < 8; i++)
            await page.Keyboard.PressAsync("ArrowLeft");
        await Assertions.Expect(readout).ToHaveTextAsync("\u22122.5 s");
        await Assertions.Expect(slider).ToHaveAttributeAsync("aria-valuetext", "2.5 seconds earlier");
        await WaitForOffsetAsync(offsets, "-2.500");
        // Focus in the sheet keeps the arrows from the player: the playhead does not move.
        Assert.Equal(playhead, await video.EvaluateAsync<double>("v => v.currentTime"));

        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "chat-sync-settings");

        // A pointer drag redraws as it goes and applies once, on release.
        while (offsets.TryDequeue(out _)) { }
        LocatorBoundingBoxResult box = (await slider.BoundingBoxAsync())!;
        float y = box.Y + box.Height / 2;
        await page.Mouse.MoveAsync(box.X + box.Width / 2, y);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(box.X + box.Width * 0.6f, y, new() { Steps = 4 });
        await page.Mouse.MoveAsync(box.X + box.Width * 0.75f, y, new() { Steps = 4 });
        await Assertions.Expect(readout).ToHaveTextAsync(new Regex("^\\+"));
        // Proving that nothing is fetched mid-drag needs a quiet period; 300 ms is several
        // times a keyed fetch's round trip against the local Mock app.
        await page.WaitForTimeoutAsync(300);
        Assert.Empty(offsets);
        await page.Mouse.UpAsync();
        string value = await slider.InputValueAsync();
        string dragged = double.Parse(value, CultureInfo.InvariantCulture).ToString("F3", CultureInfo.InvariantCulture);
        await WaitForOffsetAsync(offsets, dragged);

        // The offset is the viewer's preference: a reload keeps it, for the pane and the sheet.
        while (offsets.TryDequeue(out _)) { }
        await page.ReloadAsync();
        await WaitForOffsetAsync(offsets, dragged);
        await Assertions.Expect(slider).ToHaveValueAsync(value, new() { Timeout = 20_000 });

        await sheet.GetByRole(AriaRole.Button, new() { Name = "Reset chat sync to 0", Exact = true }).ClickAsync();
        await Assertions.Expect(slider).ToHaveValueAsync("0");
        await Assertions.Expect(readout).ToHaveTextAsync("0.0 s");
        await Assertions.Expect(slider).ToHaveAttributeAsync("aria-valuetext", "0 seconds");
        await WaitForOffsetAsync(offsets, "0.000");
    }

    private static async Task WaitForOffsetAsync(
        ConcurrentQueue<string> offsets, string expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!offsets.Contains(expected))
        {
            Assert.True(DateTime.UtcNow < deadline,
                $"no chat request with syncOffsetSec={expected}; saw {string.Join(", ", offsets)}");
            await Task.Delay(50);
        }
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
            LibraryHooks.Row, new() { HasTextString = "Community day" });
        string id = (await growing.GetAttributeAsync("data-recording-id"))!;
        await growing.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await page.Locator("[data-sk='live']").WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        ILocator pane = page.Locator("[data-sk-chat]");
        if (await pane.GetAttributeAsync("data-open") != "true")
            await page.Locator("[data-sk-player] [data-sk='chat']").ClickAsync();
        await page.Locator("[data-sk='live']").ClickAsync();
        await page.Locator("[data-sk='video']").EvaluateAsync(
            "v => { v.muted = true; return v.play(); }");

        // The Mock source writes a message every 0.5 s near the live edge.
        await page.Locator("[data-sk='chat-log'] [data-message-id]").First
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
            LibraryHooks.Row, new() { HasTextString = "Community day" });
        string id = (await growing.GetAttributeAsync("data-recording-id"))!;
        await growing.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await page.Locator("[data-sk='live']").WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        ILocator pane = page.Locator("[data-sk-chat]");
        if (await pane.GetAttributeAsync("data-open") != "true")
            await page.Locator("[data-sk-player] [data-sk='chat']").ClickAsync();
        await page.Locator("[data-sk='live']").ClickAsync();
        await page.Locator("[data-sk='video']").EvaluateAsync(
            "v => { v.muted = true; return v.play(); }");

        // Wait until the rendered rows are taller than the player, so the log must overflow.
        await page.WaitForFunctionAsync(
            """
            () => {
              const rows = [...document.querySelectorAll('[data-sk=chat-log] [data-message-id]')];
              const player = document.querySelector('[data-sk-player]').getBoundingClientRect();
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
              const player = document.querySelector('[data-sk-player]').getBoundingClientRect();
              const log = document.querySelector('[data-sk=chat-log]');
              const footer = document.querySelector('[data-sk=chat-footer]').getBoundingClientRect();
              const rows = log.querySelectorAll('[data-message-id]');
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

        ILocator saved = page.Locator(LibraryHooks.SavedRow).First;
        string id = (await saved.GetAttributeAsync("data-recording-id"))!;
        await saved.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await page.Locator("[data-sk='seek'][aria-disabled='false']")
            .WaitForAsync(new() { Timeout = 20_000 });

        ILocator pane = page.Locator("[data-sk-chat]");
        if (await pane.GetAttributeAsync("data-open") != "true")
            await page.Locator("[data-sk-player] [data-sk='chat']").ClickAsync();

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
              const rows = log.querySelectorAll('[data-message-id]');
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

    [Fact]
    public async Task The_chat_pane_uses_the_library_drawer_background()
    {
        await using LibraryJourneyTests.Session session =
            await LibraryJourneyTests.OpenAsync(app.BaseUrl);
        IPage page = session.Page;

        ILocator saved = page.Locator(LibraryHooks.SavedRow).First;
        string id = (await saved.GetAttributeAsync("data-recording-id"))!;
        await saved.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));
        await page.Locator("[data-sk='seek'][aria-disabled='false']")
            .WaitForAsync(new() { Timeout = 20_000 });

        string chat = await page.Locator("[data-sk-chat]")
            .EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor");
        Assert.Equal(await TokenColourAsync(page, "--drawer-background"), chat);
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

    private static Task<string> RingAsync(ILocator element) => element.EvaluateAsync<string>(
        "el => el.matches(':focus-visible') && getComputedStyle(el).outlineStyle === 'solid' && getComputedStyle(el).outlineWidth === '2px' ? getComputedStyle(el).outlineColor : 'no ring'");

    private static Task<string> TokenColourAsync(IPage page, string token) => page.EvaluateAsync<string>(
        """
        token => {
          const probe = document.createElement('span');
          probe.style.color = `var(${token})`;
          document.body.append(probe);
          const colour = getComputedStyle(probe).color;
          probe.remove();
          return colour;
        }
        """, token);

    private sealed class ChannelColours
    {
        public string Chat { get; set; } = "";
        public string Drawer { get; set; } = "";
        public string Shown { get; set; } = "";
    }
}
