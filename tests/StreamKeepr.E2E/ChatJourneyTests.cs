using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Playwright;

namespace StreamKeepr.E2E;

[Collection(MockAppCollection.Name)]
[CaptureFailure]
[Trait("Category", "E2E")]
public sealed class ChatJourneyTests(MockAppFixture app)
{
    // Design system: the chat drawer's header is 64px and its Close control a 40px button.
    private const double HeaderHeight = 64;
    private const double CloseSize = 40;

    [Fact]
    public async Task Opens_the_chat_pane_seeks_and_persists_the_open_state()
    {
        await using Journey session =
            await LibraryJourneyTests.OpenAsync(app);
        IPage page = session.Page;

        ILocator saved = (await LibraryHooks.RowAsync(page, LibraryHooks.SavedRow));
        string id = (await saved.GetAttributeAsync("data-recording-id"))!;
        await session.OpenRecordingAsync(saved);

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

        await session.ReloadAsync();
        await page.Locator("[data-sk='seek'][aria-disabled='false']")
            .WaitForAsync(new() { Timeout = 20_000 });
        Assert.Equal("true", await page.Locator("[data-sk-chat]").GetAttributeAsync("data-open"));
        Assert.Equal("true", await page.Locator("[data-sk-player] [data-sk='chat']")
            .GetAttributeAsync("aria-pressed"));
    }

    [Fact]
    public async Task The_player_chat_button_mirrors_C_and_the_streamer_speaks_in_the_channel_colour()
    {
        await using Journey session =
            await LibraryJourneyTests.OpenAsync(app);
        IPage page = session.Page;

        ILocator saved = (await LibraryHooks.RowAsync(page, LibraryHooks.SavedRow));
        string id = (await saved.GetAttributeAsync("data-recording-id"))!;
        await session.OpenRecordingAsync(saved);

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
              // The watched channel's option carries data-current.
              const avatar = document.querySelector('nav[aria-label=Library] [data-library-channel][data-current="true"] [data-channel-color]');
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
    public async Task The_drawer_header_closes_persists_and_returns_focus_to_the_player()
    {
        await using Journey session =
            await LibraryJourneyTests.OpenAsync(app);
        IPage page = session.Page;

        ILocator saved = (await LibraryHooks.RowAsync(page, LibraryHooks.SavedRow));
        await session.OpenRecordingAsync(saved);

        ILocator pane = await OpenDrawerAsync(page);

        // The header is 64px and ends with a 40px Close control named "Close chat".
        ILocator head = page.Locator("[data-sk-chat] > header");
        Assert.Equal(HeaderHeight, Math.Round((await head.BoundingBoxAsync())!.Height));
        ILocator close = page.GetByRole(AriaRole.Button, new() { Name = "Close chat", Exact = true });
        await close.WaitForAsync();
        LocatorBoundingBoxResult closeBox = (await close.BoundingBoxAsync())!;
        Assert.Equal(CloseSize, Math.Round(closeBox.Width));
        Assert.Equal(CloseSize, Math.Round(closeBox.Height));

        // Focus elsewhere first: the Close control must return focus to the Chat button.
        await page.Locator("[data-sk='seek']").FocusAsync();
        await close.ClickAsync();
        await Assertions.Expect(pane).ToHaveAttributeAsync("data-open", "false");
        Assert.True(await FocusedIsChatButtonAsync(page),
            "closing from the header returns focus to the player's Chat button");

        // The sk-ui cookie is written asynchronously; wait for the closed state before reloading.
        await page.WaitForFunctionAsync(
            """() => decodeURIComponent(document.cookie).includes('"chat":false')""");

        await session.ReloadAsync();
        await page.Locator("[data-sk='seek'][aria-disabled='false']")
            .WaitForAsync(new() { Timeout = 20_000 });
        Assert.Equal("false", await page.Locator("[data-sk-chat]").GetAttributeAsync("data-open"));
    }

    [Fact]
    public async Task Escape_closes_the_drawer_only_when_nothing_else_owns_it()
    {
        await using Journey session =
            await LibraryJourneyTests.OpenAsync(app);
        IPage page = session.Page;

        ILocator saved = (await LibraryHooks.RowAsync(page, LibraryHooks.SavedRow));
        await session.OpenRecordingAsync(saved);

        ILocator pane = await OpenDrawerAsync(page);

        // With the library open, Escape closes the library and leaves chat open.
        ILocator drawer = page.Locator(LibraryHooks.Drawer);
        await page.Locator("[data-drawer-toggle]").ClickAsync();
        await LibraryJourneyTests.Expect(
            async () => await drawer.GetAttributeAsync("data-drawer") == "expanded" ? 1 : 0, 1);
        await page.Locator("[data-sk='chat-log']").FocusAsync();
        await page.Keyboard.PressAsync("Escape");
        await LibraryJourneyTests.Expect(
            async () => await drawer.GetAttributeAsync("data-drawer") == "rail" ? 1 : 0, 1);
        await Assertions.Expect(pane).ToHaveAttributeAsync("data-open", "true");

        // With nothing else open, Escape closes the drawer and returns focus from inside it.
        await page.Locator("[data-sk='chat-log']").FocusAsync();
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(pane).ToHaveAttributeAsync("data-open", "false");
        Assert.True(await FocusedIsChatButtonAsync(page),
            "Escape from inside the drawer returns focus to the player's Chat button");
    }

    private static Task<bool> FocusedIsChatButtonAsync(IPage page) =>
        page.EvaluateAsync<bool>(
            """() => document.activeElement === document.querySelector("[data-sk-player] [data-sk='chat']")""");

    /// <summary>Opens the watch recording's chat drawer with the player's Chat button.</summary>
    private static async Task<ILocator> OpenDrawerAsync(IPage page)
    {
        ILocator pane = page.Locator("[data-sk-chat]");
        ILocator chat = page.Locator("[data-sk-player]")
            .GetByRole(AriaRole.Button, new() { Name = "Chat", Exact = true });
        await chat.WaitForAsync();
        if (await pane.GetAttributeAsync("data-open") != "true")
            await chat.ClickAsync();
        await Assertions.Expect(pane).ToHaveAttributeAsync("data-open", "true");
        return pane;
    }

    [Fact]
    public async Task Chat_sync_in_settings_applies_live_to_the_open_pane_and_persists()
    {
        await using Journey session =
            await LibraryJourneyTests.OpenAsync(app);
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

        ILocator saved = (await LibraryHooks.RowAsync(page, LibraryHooks.SavedRow));
        string id = (await saved.GetAttributeAsync("data-recording-id"))!;
        await session.OpenRecordingAsync(saved);
        ILocator pane = page.Locator("[data-sk-chat]");
        if (await pane.GetAttributeAsync("data-open") != "true")
            await page.Locator("[data-sk-player] [data-sk='chat']").ClickAsync();
        await WaitForOffsetAsync(offsets, "0.000");
        // The pane holds no sync control of its own.
        Assert.Equal(0, await pane.GetByRole(AriaRole.Slider).CountAsync());

        // Pause so a leaked arrow key shows as a jump rather than normal playback.
        ILocator video = page.Locator("[data-sk='video']");
        if (!await video.EvaluateAsync<bool>("v => v.paused"))
        {
            await page.EvaluateAsync("() => document.activeElement?.blur()");
            await page.Keyboard.PressAsync("k");
        }
        Assert.True(await video.EvaluateAsync<bool>("v => v.paused"));

        // Settings opens over the recording; the pane stays behind it.
        await page.GetByRole(AriaRole.Banner)
            .GetByRole(AriaRole.Link, new() { Name = "Settings", Exact = true }).ClickAsync();
        ILocator sheet = page.GetByRole(AriaRole.Complementary, new() { Name = "Settings", Exact = true });
        ILocator slider = sheet.GetByRole(AriaRole.Slider, new() { Name = "Chat sync", Exact = true });
        ILocator readout = sheet.Locator("[data-chat-sync-value]");
        await Assertions.Expect(slider).ToBeVisibleAsync(new() { Timeout = 20_000 });

        double playhead = await video.EvaluateAsync<double>("v => v.currentTime");
        await slider.FocusAsync();
        for (int i = 0; i < 3; i++)
            await page.Keyboard.PressAsync("ArrowRight");
        await Assertions.Expect(readout).ToHaveTextAsync("+1.5 s");
        await Assertions.Expect(slider).ToHaveAttributeAsync("aria-valuetext", "1.5 seconds later");
        // The open pane behind the sheet refetches with the new offset at once.
        await WaitForOffsetAsync(offsets, "1.500");

        // Rapid presses can outrun the circuit's per-step round trip; the contract is one
        // rendered step per press, so wait for each step rather than requiring lossless
        // rapid input (#296).
        for (int i = 0; i < 8; i++)
        {
            string before = await slider.InputValueAsync();
            await page.Keyboard.PressAsync("ArrowLeft");
            await Assertions.Expect(slider).Not.ToHaveValueAsync(before, new() { Timeout = 5_000 });
        }
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
        await session.ReloadAsync();
        await WaitForOffsetAsync(offsets, dragged);
        await Assertions.Expect(slider).ToHaveValueAsync(value, new() { Timeout = 20_000 });

        await sheet.GetByRole(AriaRole.Button, new() { Name = "Reset chat sync to 0", Exact = true }).ClickAsync();
        await Assertions.Expect(slider).ToHaveValueAsync("0");
        await Assertions.Expect(readout).ToHaveTextAsync("0.0 s");
        await Assertions.Expect(slider).ToHaveAttributeAsync("aria-valuetext", "0 seconds");
        await WaitForOffsetAsync(offsets, "0.000");
    }

    [Fact]
    public async Task Show_badges_hides_images_live_and_persists()
    {
        await using Journey session = await LibraryJourneyTests.OpenAsync(app);
        IPage page = session.Page;
        var chatRequests = new ConcurrentQueue<string>();
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("/chat?", StringComparison.Ordinal))
                chatRequests.Enqueue(request.Url);
        };

        const string ImageHost = Journey.MockBadgeImageHost;
        await page.RouteAsync($"{ImageHost}**", async route =>
        {
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "image/svg+xml",
                Body = Journey.MockBadgeImage,
            });
        });

        // The first saved channel is not necessarily the one with a custom badge catalog; open
        // a mock_nova recording so the channel scope has entries to render.
        ILocator saved = await LibraryHooks.RowAsync(
            page, LibraryHooks.SavedRow, hasText: "Ranked climb to Diamond");
        await session.OpenRecordingAsync(saved);
        ILocator pane = page.Locator("[data-sk-chat]");
        if (await pane.GetAttributeAsync("data-open") != "true")
            await page.Locator("[data-sk-player] [data-sk='chat']").ClickAsync();

        // The seeded chat holds a moderator message and the streamer's broadcaster message.
        await SeekToChatAsync(page);
        ILocator moderatorBadge = page.Locator("[data-message-id='seed-1']")
            .GetByRole(AriaRole.Img, new() { Name = "Moderator", Exact = true });
        ILocator broadcasterBadge = page.Locator("[data-message-id='seed-streamer']")
            .GetByRole(AriaRole.Img, new() { Name = "Broadcaster", Exact = true });
        await Assertions.Expect(moderatorBadge).ToBeVisibleAsync(new() { Timeout = 20_000 });
        await Assertions.Expect(broadcasterBadge).ToBeVisibleAsync();
        await WaitForDecodedAsync(moderatorBadge);
        await WaitForDecodedAsync(broadcasterBadge);
        Assert.Equal(
            $"{ImageHost}badges/moderator/1/1",
            await moderatorBadge.GetAttributeAsync("src"));

        // The resolved channel's catalog wins for subscriber/0 and supplies subscriber/3 and
        // bits/100; subscriber/12 comes from global, and subscriber/99 keeps the text chip.
        ILocator channelSubscriber = page.Locator("[data-message-id='seed-channel-0']")
            .GetByRole(AriaRole.Img, new() { Name = "Nova Subscriber", Exact = true });
        ILocator channelMonths = page.Locator("[data-message-id='seed-channel-3']")
            .GetByRole(AriaRole.Img, new() { Name = "Nova Subscriber 3 months", Exact = true });
        ILocator channelBits = page.Locator("[data-message-id='seed-bits']")
            .GetByRole(AriaRole.Img, new() { Name = "100 Bits", Exact = true });
        await Assertions.Expect(channelSubscriber).ToBeVisibleAsync(new() { Timeout = 20_000 });
        await Assertions.Expect(channelMonths).ToBeVisibleAsync();
        await Assertions.Expect(channelBits).ToBeVisibleAsync();
        await WaitForDecodedAsync(channelSubscriber);
        Assert.Equal(
            $"{ImageHost}channel/subscriber/0/1",
            await channelSubscriber.GetAttributeAsync("src"));
        Assert.Equal("lazy", await channelSubscriber.GetAttributeAsync("loading"));
        Assert.Equal("async", await channelSubscriber.GetAttributeAsync("decoding"));
        Assert.Equal("no-referrer", await channelSubscriber.GetAttributeAsync("referrerpolicy"));
        Assert.Equal(
            $"{ImageHost}channel/subscriber/3/1",
            await channelMonths.GetAttributeAsync("src"));
        Assert.Equal(
            $"{ImageHost}channel/bits/100/1",
            await channelBits.GetAttributeAsync("src"));

        // The channel omits subscriber/12, so the pane uses the global catalog at that exact version.
        ILocator globalSubscriber = page.Locator("[data-message-id='seed-global-12']")
            .GetByRole(AriaRole.Img, new() { Name = "Subscriber", Exact = true });
        await Assertions.Expect(globalSubscriber).ToBeVisibleAsync(new() { Timeout = 20_000 });
        Assert.Equal(
            $"{ImageHost}badges/subscriber/12/1",
            await globalSubscriber.GetAttributeAsync("src"));

        // subscriber/99 exists in neither scope, so it keeps the text chip.
        ILocator missingChip = page.Locator("[data-message-id='seed-unmatched']")
            .GetByText("SUB", new() { Exact = true });
        await Assertions.Expect(missingChip).ToHaveTextAsync("SUB");
        Assert.Equal(
            0,
            await page.Locator("[data-message-id='seed-unmatched']").Locator("img").CountAsync());
        string srcset = (await moderatorBadge.GetAttributeAsync("srcset"))!;
        Assert.Contains($"{ImageHost}badges/moderator/1/2 2x", srcset, StringComparison.Ordinal);
        Assert.Contains($"{ImageHost}badges/moderator/1/3 4x", srcset, StringComparison.Ordinal);
        Assert.Equal("lazy", await moderatorBadge.GetAttributeAsync("loading"));
        Assert.Equal("async", await moderatorBadge.GetAttributeAsync("decoding"));
        Assert.Equal("no-referrer", await moderatorBadge.GetAttributeAsync("referrerpolicy"));
        await Assertions.Expect(pane).ToHaveAttributeAsync("data-badges", "true");

        // A catalog badge replaces its text chip rather than joining it.
        Assert.Equal(
            0,
            await page.Locator("[data-message-id='seed-1']")
                .GetByText("MOD", new() { Exact = true }).CountAsync());

        ILocator toggle = await OpenSettingsAsync(page, page
            .GetByRole(AriaRole.Complementary, new() { Name = "Settings", Exact = true })
            .GetByRole(AriaRole.Checkbox, new() { Name = "Show badges", Exact = true }));
        await Assertions.Expect(toggle).ToBeCheckedAsync(new() { Timeout = 20_000 });

        while (chatRequests.TryDequeue(out _)) { }
        await toggle.UncheckAsync();
        await page.WaitForFunctionAsync(
            """
            () => [...document.querySelectorAll("[data-message-id='seed-1'] img, [data-message-id='seed-streamer'] img, [data-message-id='seed-channel-0'] img")]
              .every((image) => getComputedStyle(image).display === "none")
            """);
        await Assertions.Expect(pane).ToHaveAttributeAsync("data-badges", "false");
        await Assertions.Expect(channelSubscriber).ToBeHiddenAsync();
        // Visibility only: the open pane keeps its loaded chat and does not refetch.
        Assert.Empty(chatRequests);

        // The preference is the viewer's: a reload keeps it for the pane and for the sheet.
        await session.ReloadAsync();
        await Assertions.Expect(page.Locator("[data-sk-chat]"))
            .ToHaveAttributeAsync("data-badges", "false", new() { Timeout = 20_000 });
        await SeekToChatAsync(page);
        ILocator reloadedBadge = page.Locator("[data-message-id='seed-1']").Locator("img");
        // Attached first: ToBeHidden also passes for a node that is not there at all.
        await reloadedBadge.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 20_000 });
        await Assertions.Expect(reloadedBadge).ToBeHiddenAsync();
        ILocator reloadedToggle = await OpenSettingsAsync(page, page
            .GetByRole(AriaRole.Complementary, new() { Name = "Settings", Exact = true })
            .GetByRole(AriaRole.Checkbox, new() { Name = "Show badges", Exact = true }));
        await Assertions.Expect(reloadedToggle).Not.ToBeCheckedAsync(new() { Timeout = 20_000 });

        // The reload left the setting off; turning it back on restores the images.
        await reloadedToggle.CheckAsync();
        await Assertions.Expect(moderatorBadge).ToBeVisibleAsync(new() { Timeout = 10_000 });
        await Assertions.Expect(page.Locator("[data-sk-chat]"))
            .ToHaveAttributeAsync("data-badges", "true");

        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "chat-badges");

        async Task WaitForDecodedAsync(ILocator image) =>
            await page.WaitForFunctionAsync(
                "img => img.complete && img.naturalWidth > 0",
                await image.ElementHandleAsync());

    }

    [Fact]
    public async Task Chat_font_settings_scale_text_badges_and_persist()
    {
        await using Journey session = await LibraryJourneyTests.OpenAsync(app);
        IPage page = session.Page;
        var chatRequests = new ConcurrentQueue<string>();
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("/chat?", StringComparison.Ordinal))
                chatRequests.Enqueue(request.Url);
        };

        const string ImageHost = Journey.MockBadgeImageHost;
        await page.RouteAsync($"{ImageHost}**", async route =>
        {
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "image/svg+xml",
                Body = Journey.MockBadgeImage,
            });
        });

        // The mock_nova recording carries the badge catalog used by the seeding.
        ILocator saved = await LibraryHooks.RowAsync(
            page, LibraryHooks.SavedRow, hasText: "Ranked climb to Diamond");
        await session.OpenRecordingAsync(saved);
        ILocator pane = page.Locator("[data-sk-chat]");
        if (await pane.GetAttributeAsync("data-open") != "true")
            await page.Locator("[data-sk-player] [data-sk='chat']").ClickAsync();

        await SeekToChatAsync(page);
        ILocator row = page.Locator("[data-message-id='seed-1']");
        ILocator name = row.Locator("[data-chat-name]");
        ILocator badgeImage = row.GetByRole(AriaRole.Img, new() { Name = "Moderator", Exact = true });
        ILocator chip = page.Locator("[data-message-id='seed-unmatched']")
            .GetByText("SUB", new() { Exact = true });
        ILocator log = pane.Locator("[data-sk='chat-log']");
        await Assertions.Expect(badgeImage).ToBeVisibleAsync(new() { Timeout = 20_000 });
        await chip.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 20_000 });

        // Pause so a leaked arrow key shows as a jump rather than normal playback.
        ILocator video = page.Locator("[data-sk='video']");
        if (!await video.EvaluateAsync<bool>("v => v.paused"))
        {
            await page.EvaluateAsync("() => document.activeElement?.blur()");
            await page.Keyboard.PressAsync("k");
        }
        Assert.True(await video.EvaluateAsync<bool>("v => v.paused"));
        double playhead = await video.EvaluateAsync<double>("v => v.currentTime");

        // Baseline 100%/400: body2 messages, 700 names, an 18px badge image and a 12px chip.
        Assert.InRange(await log.EvaluateAsync<double>("el => parseFloat(getComputedStyle(el).fontSize)"), 13.9, 14.1);
        Assert.InRange(await name.EvaluateAsync<double>("el => parseFloat(getComputedStyle(el).fontSize)"), 13.9, 14.1);
        Assert.Equal("400", await row.EvaluateAsync<string>("el => getComputedStyle(el).fontWeight"));
        Assert.Equal("700", await name.EvaluateAsync<string>("el => getComputedStyle(el).fontWeight"));
        Assert.InRange(await badgeImage.EvaluateAsync<double>("el => el.getBoundingClientRect().height"), 17.9, 18.1);
        Assert.InRange(await chip.EvaluateAsync<double>("el => parseFloat(getComputedStyle(el).fontSize)"), 11.9, 12.1);
        Assert.Equal("500", await chip.EvaluateAsync<string>("el => getComputedStyle(el).fontWeight"));

        // Settings opens over the recording; the pane stays behind it.
        ILocator sheet = page.GetByRole(AriaRole.Complementary, new() { Name = "Settings", Exact = true });
        ILocator size = sheet.GetByRole(AriaRole.Slider, new() { Name = "Chat font size", Exact = true });
        ILocator readout = sheet.Locator("[data-chat-font-value]");
        ILocator weight = sheet.GetByRole(AriaRole.Combobox, new() { Name = "Chat font weight", Exact = true });
        await OpenSettingsAsync(page, size);
        Assert.Equal("100", await size.InputValueAsync());
        while (chatRequests.TryDequeue(out _)) { }

        // Below 100% the text shrinks; the badge image and chip keep their design size.
        await size.FocusAsync();
        await page.Keyboard.PressAsync("ArrowLeft");
        await Assertions.Expect(readout).ToHaveTextAsync("95%");
        await page.Keyboard.PressAsync("ArrowLeft");
        await Assertions.Expect(readout).ToHaveTextAsync("90%");
        await Assertions.Expect(size).ToHaveAttributeAsync("aria-valuetext", "90 percent");
        await Assertions.Expect(pane).ToHaveAttributeAsync("data-font-scale", "90");
        Assert.InRange(await log.EvaluateAsync<double>("el => parseFloat(getComputedStyle(el).fontSize)"), 12.5, 12.7);
        Assert.InRange(await badgeImage.EvaluateAsync<double>("el => el.getBoundingClientRect().height"), 17.9, 18.1);
        Assert.InRange(await chip.EvaluateAsync<double>("el => parseFloat(getComputedStyle(el).fontSize)"), 11.9, 12.1);

        // Above 100% the badge image and chip scale up with the message text.
        await page.Keyboard.PressAsync("ArrowRight");
        await Assertions.Expect(readout).ToHaveTextAsync("95%");
        await page.Keyboard.PressAsync("ArrowRight");
        await Assertions.Expect(readout).ToHaveTextAsync("100%");
        await page.Keyboard.PressAsync("ArrowRight");
        await Assertions.Expect(readout).ToHaveTextAsync("105%");
        await Assertions.Expect(size).ToHaveAttributeAsync("aria-valuetext", "105 percent");
        await Assertions.Expect(pane).ToHaveAttributeAsync("data-font-scale", "105");
        Assert.InRange(await log.EvaluateAsync<double>("el => parseFloat(getComputedStyle(el).fontSize)"), 14.6, 14.8);
        Assert.InRange(await badgeImage.EvaluateAsync<double>("el => el.getBoundingClientRect().height"), 18.8, 19.0);
        Assert.InRange(await chip.EvaluateAsync<double>("el => parseFloat(getComputedStyle(el).fontSize)"), 12.5, 12.7);
        Assert.True(await log.EvaluateAsync<bool>(
            "el => el.scrollHeight - el.scrollTop - el.clientHeight < 24"));
        // The open pane applies it without a chat refetch, and playback does not move. 300 ms
        // is several times a keyed fetch's round trip against the local Mock app.
        await page.WaitForTimeoutAsync(300);
        Assert.Empty(chatRequests);
        Assert.Equal(playhead, await video.EvaluateAsync<double>("v => v.currentTime"));

        await weight.SelectOptionAsync(new SelectOptionValue { Label = "Bold (700)" });
        await Assertions.Expect(pane).ToHaveAttributeAsync("data-font-weight", "700", new() { Timeout = 10_000 });
        Assert.Equal("700", await name.EvaluateAsync<string>("el => getComputedStyle(el).fontWeight"));
        Assert.Equal("500", await chip.EvaluateAsync<string>("el => getComputedStyle(el).fontWeight"));
        Assert.Equal("700", await row.EvaluateAsync<string>("el => getComputedStyle(el).fontWeight"));
        // The same quiet period after the weight change.
        await page.WaitForTimeoutAsync(300);
        Assert.Empty(chatRequests);
        Assert.Equal(playhead, await video.EvaluateAsync<double>("v => v.currentTime"));

        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "chat-font-settings");

        // The viewer's preference: a reload keeps both the pane's typography and the controls.
        await session.ReloadAsync();
        ILocator reloadedPane = page.Locator("[data-sk-chat]");
        await Assertions.Expect(reloadedPane).ToHaveAttributeAsync(
            "data-font-scale", "105", new() { Timeout = 20_000 });
        await Assertions.Expect(reloadedPane).ToHaveAttributeAsync("data-font-weight", "700");
        await SeekToChatAsync(page);
        ILocator reloadedBadge = page.Locator("[data-message-id='seed-1']")
            .GetByRole(AriaRole.Img, new() { Name = "Moderator", Exact = true });
        await Assertions.Expect(reloadedBadge).ToBeVisibleAsync(new() { Timeout = 20_000 });
        Assert.InRange(await reloadedBadge.EvaluateAsync<double>("el => el.getBoundingClientRect().height"), 18.8, 19.0);

        ILocator reloadedSize = page.GetByRole(AriaRole.Complementary, new() { Name = "Settings", Exact = true })
            .GetByRole(AriaRole.Slider, new() { Name = "Chat font size", Exact = true });
        await OpenSettingsAsync(page, reloadedSize);
        Assert.Equal("105", await reloadedSize.InputValueAsync());
        Assert.Equal("700", await page.GetByRole(AriaRole.Complementary, new() { Name = "Settings", Exact = true })
            .GetByRole(AriaRole.Combobox, new() { Name = "Chat font weight", Exact = true })
            .InputValueAsync());
    }

    private static async Task<ILocator> OpenSettingsAsync(IPage page, ILocator target)
    {
        if (!await target.IsVisibleAsync())
        {
            await page.GetByRole(AriaRole.Banner)
                .GetByRole(AriaRole.Link, new() { Name = "Settings", Exact = true }).ClickAsync();
        }

        await Assertions.Expect(target).ToBeVisibleAsync(new() { Timeout = 20_000 });
        return target;
    }

    private static async Task SeekToChatAsync(IPage page)
    {
        await page.Locator("[data-sk='seek'][aria-disabled='false']")
            .WaitForAsync(new() { Timeout = 20_000 });
        ILocator seek = page.Locator("[data-sk='seek']");
        await seek.FocusAsync();
        await page.Keyboard.PressAsync("Home");
        for (int i = 0; i < 4; i++)
            await page.Keyboard.PressAsync("ArrowRight");
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
        await using Journey session =
            await LibraryJourneyTests.OpenAsync(app);
        IPage page = session.Page;
        int chatRequests = 0;
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("/chat?", StringComparison.Ordinal))
                Interlocked.Increment(ref chatRequests);
        };

        ILocator growing = (await LibraryHooks.RowAsync(page, LibraryHooks.Row, "Community day"));
        string id = (await growing.GetAttributeAsync("data-recording-id"))!;
        await session.OpenRecordingAsync(growing);
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
    public async Task Live_chat_scrolls_inside_a_full_height_column_without_a_footer_at_the_bottom()
    {
        await using Journey session =
            await LibraryJourneyTests.OpenAsync(app);
        IPage page = session.Page;

        ILocator growing = (await LibraryHooks.RowAsync(page, LibraryHooks.Row, "Community day"));
        await session.OpenRecordingAsync(growing);
        await page.Locator("[data-sk='live']").WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        ILocator pane = page.Locator("[data-sk-chat]");
        if (await pane.GetAttributeAsync("data-open") != "true")
            await page.Locator("[data-sk-player] [data-sk='chat']").ClickAsync();
        await page.Locator("[data-sk='live']").ClickAsync();
        await page.Locator("[data-sk='video']").EvaluateAsync(
            "v => { v.muted = true; return v.play(); }");

        // Wait until the rendered rows are taller than the log, so it must overflow, and the
        // follow scroll has brought the newest row into view.
        await page.WaitForFunctionAsync(
            """
            () => {
              const log = document.querySelector('[data-sk=chat-log]');
              const rows = [...log.querySelectorAll('[data-message-id]')];
              const total = rows.reduce((sum, row) => sum + row.getBoundingClientRect().height, 0);
              return total > log.clientHeight &&
                rows[rows.length - 1].getBoundingClientRect().bottom <= log.getBoundingClientRect().bottom + 1;
            }
            """,
            null,
            new() { Timeout = 45_000 });

        LiveLayout layout = await page.EvaluateAsync<LiveLayout>(
            """
            () => {
              const pane = document.querySelector('[data-sk-chat]').getBoundingClientRect();
              const player = document.querySelector('[data-sk-player]').getBoundingClientRect();
              const main = document.querySelector('main').getBoundingClientRect();
              const log = document.querySelector('[data-sk=chat-log]');
              const rows = log.querySelectorAll('[data-message-id]');
              const last = rows[rows.length - 1].getBoundingClientRect();
              const logBox = log.getBoundingClientRect();
              return {
                paneHeight: pane.height,
                playerHeight: player.height,
                paneBottom: pane.bottom,
                playerBottom: player.bottom,
                viewBottom: main.bottom,
                scrollable: log.scrollHeight > log.clientHeight,
                lastRowBottom: last.bottom,
                logBottom: logBox.bottom,
              };
            }
            """);

        // The pane is the view's height (design system: AppShell, chat as a full-height column)
        // instead of growing with its content, so it stays inside the view.
        Assert.True(layout.PaneHeight > layout.PlayerHeight,
            $"chat pane {layout.PaneHeight}px is no taller than the player {layout.PlayerHeight}px");
        Assert.True(layout.PaneBottom <= layout.ViewBottom + 1,
            $"chat pane bottom {layout.PaneBottom}px is below the view {layout.ViewBottom}px");
        Assert.True(layout.Scrollable, "the chat log scrolls once it overflows");
        // Following the live edge keeps the newest row in view at the bottom of the log, and
        // there is no footer to reserve space while the viewer is at the current time.
        Assert.True(layout.LastRowBottom <= layout.LogBottom + 1,
            $"newest row bottom {layout.LastRowBottom}px is below the log {layout.LogBottom}px");
        Assert.Equal(0, await page.Locator("[data-sk='chat-footer']").CountAsync());
    }

    [Fact]
    public async Task Pause_scroll_up_and_resume_returns_chat_to_the_playhead()
    {
        await using Journey session =
            await LibraryJourneyTests.OpenAsync(app);
        IPage page = session.Page;

        ILocator growing = (await LibraryHooks.RowAsync(page, LibraryHooks.Row, "Community day"));
        await session.OpenRecordingAsync(growing);
        await page.Locator("[data-sk='live']").WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        ILocator pane = page.Locator("[data-sk-chat]");
        if (await pane.GetAttributeAsync("data-open") != "true")
            await page.Locator("[data-sk-player] [data-sk='chat']").ClickAsync();
        await page.Locator("[data-sk='live']").ClickAsync();
        ILocator video = page.Locator("[data-sk='video']");
        await video.EvaluateAsync("v => { v.muted = true; return v.play(); }");

        // Overflow the log and follow the newest row before pausing.
        await page.WaitForFunctionAsync(
            """
            () => {
              const log = document.querySelector('[data-sk=chat-log]');
              const rows = [...log.querySelectorAll('[data-message-id]')];
              const total = rows.reduce((sum, row) => sum + row.getBoundingClientRect().height, 0);
              return total > log.clientHeight &&
                rows[rows.length - 1].getBoundingClientRect().bottom <= log.getBoundingClientRect().bottom + 1;
            }
            """,
            null,
            new() { Timeout = 45_000 });

        await video.EvaluateAsync("v => v.pause()");
        await page.WaitForFunctionAsync(
            "() => document.querySelector('[data-sk=video]').paused === true");
        // The pause event drives the chat's previous-paused state; wait until the player has
        // rendered it before the scroll, so the resume cannot be mistaken for a normal tick.
        await page.Locator("[data-sk-player] [data-sk='play'][aria-label='Play']")
            .WaitForAsync(new() { Timeout = 5_000 });

        // The wheel intent stops any glide; the scroll then reads as the viewer leaving.
        await page.Locator("[data-sk='chat-log']").EvaluateAsync(
            """
            (log) => {
              log.dispatchEvent(new WheelEvent('wheel', { deltaY: -100, bubbles: true }));
              log.scrollTop = 0;
              log.dispatchEvent(new Event('scroll'));
            }
            """);

        ILocator footer = page.Locator("[data-sk='chat-footer']");
        await footer.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        Assert.Equal("Jump to current time", (await footer.InnerTextAsync()).Trim());
        bool centered = await footer.EvaluateAsync<bool>(
            """
            (footer) => {
              const button = footer.querySelector('[data-sk=chat-jump]');
              const f = footer.getBoundingClientRect();
              const b = button.getBoundingClientRect();
              return Math.abs((b.left + b.right) / 2 - (f.left + f.right) / 2) <= 1;
            }
            """);
        Assert.True(centered, "the jump control is centered in the footer");

        await video.EvaluateAsync("v => { v.muted = true; return v.play(); }");
        await page.WaitForFunctionAsync(
            """
            () => {
              const log = document.querySelector('[data-sk=chat-log]');
              const rows = [...log.querySelectorAll('[data-message-id]')];
              const last = rows[rows.length - 1];
              return !document.querySelector('[data-sk=chat-footer]') &&
                log.scrollTop + log.clientHeight >= log.scrollHeight - 1 &&
                last.getBoundingClientRect().bottom <= log.getBoundingClientRect().bottom + 1;
            }
            """,
            null,
            new() { Timeout = 10_000 });

        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "chat-resume");
    }

    [Fact]
    public async Task Short_chat_is_anchored_to_the_bottom_of_the_log()
    {
        await using Journey session =
            await LibraryJourneyTests.OpenAsync(app);
        IPage page = session.Page;

        ILocator saved = (await LibraryHooks.RowAsync(page, LibraryHooks.SavedRow));
        string id = (await saved.GetAttributeAsync("data-recording-id"))!;
        await session.OpenRecordingAsync(saved);

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
        await using Journey session =
            await LibraryJourneyTests.OpenAsync(app);
        IPage page = session.Page;

        ILocator saved = (await LibraryHooks.RowAsync(page, LibraryHooks.SavedRow));
        string id = (await saved.GetAttributeAsync("data-recording-id"))!;
        await session.OpenRecordingAsync(saved);

        string chat = await page.Locator("[data-sk-chat]")
            .EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor");
        Assert.Equal(await TokenColourAsync(page, "--drawer-background"), chat);
    }

    // Playwright deserializes evaluate results through a parameterless constructor.
    private sealed class LiveLayout
    {
        public double PaneHeight { get; set; }
        public double PlayerHeight { get; set; }
        public double PaneBottom { get; set; }
        public double PlayerBottom { get; set; }
        public double ViewBottom { get; set; }
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
