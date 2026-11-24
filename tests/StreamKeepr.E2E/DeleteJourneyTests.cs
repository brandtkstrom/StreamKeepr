using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

[CaptureFailure]
[Trait("Category", "E2E")]
public sealed class DeleteJourneyTests : IAsyncLifetime
{
    // Deletion is destructive and the shared Mock fixture's other journeys count rows, so this
    // class owns a disposable app instead of mutating the shared library.
    private readonly MockAppFixture _app = new("delete");

    public Task InitializeAsync() => _app.InitializeAsync();

    public Task DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task An_admin_deletes_a_finished_recording_and_moves_to_the_next_one()
    {
        await using Journey session = await OpenAsync(_app);
        IPage page = session.Page;

        // The first recording in the current query: the first channel's newest.
        string topId = (await (await LibraryHooks.RowAsync(page))
            .GetAttributeAsync("data-recording-id"))!;
        ILocator row = (await LibraryHooks.RowAsync(page, LibraryHooks.Row, "Retro marathon"));
        string deletedId = (await row.GetAttributeAsync("data-recording-id"))!;
        Assert.NotEqual(topId, deletedId);

        await session.OpenRecordingAsync(row);

        // Delete lives in More actions (design system: RecordingDetails).
        ILocator delete = await TitleBlock.MenuItemAsync(page, "data-delete");
        if (await delete.GetAttributeAsync("aria-disabled") == "true")
        {
            string reason = await delete.Locator("[data-delete-blocked]").TextContentAsync() ?? "";
            Assert.Fail($"Delete was disabled: {reason}");
        }

        // Cancel first: the dialog closes, focus returns to More actions (the menu item that
        // opened it is gone), and the recording stays.
        await delete.ClickAsync();
        ILocator dialog = Feedback.Dialog(page, "Delete recording?");
        await dialog.WaitForAsync();
        Assert.Contains("Retro marathon", await page.Locator("[data-delete-banner]").InnerTextAsync());
        Assert.Contains("permanently", await page.Locator("[data-delete-banner]").InnerTextAsync());
        await new LibraryJourneyTests(_app).AssertAxeCleanAsync(page, "delete-dialog");
        await page.Locator("[data-delete-cancel]").ClickAsync();
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await Assertions.Expect(TitleBlock.More(page)).ToBeFocusedAsync();
        Assert.Equal(1, await page.Locator($"[data-selected-recording='{deletedId}']").CountAsync());

        // Confirm: the row disappears, the snackbar names the outcome, and Home opens the
        // first remaining recording in the current query.
        await (await TitleBlock.MenuItemAsync(page, "data-delete")).ClickAsync();
        await Feedback.Dialog(page, "Delete recording?").WaitForAsync();
        await page.Locator("[data-delete-confirm]").ClickAsync();

        await Feedback.Snackbar(page, "Recording deleted").WaitForAsync();
        await Journey.WaitForUrlAsync(page, url => url.EndsWith($"/r/{topId}", StringComparison.Ordinal));
        await page.Locator($"[data-selected-recording='{deletedId}']")
            .WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await new LibraryJourneyTests(_app).AssertAxeCleanAsync(page, "delete");
    }

    [Fact]
    public async Task A_row_deletes_from_the_library_with_its_own_button_and_focus_moves_on()
    {
        await using Journey session = await OpenAsync(_app);
        IPage page = session.Page;

        // A channel with two finished recordings, so one is left to take focus.
        ILocator target = await LibraryHooks.RowAsync(page, LibraryHooks.Row, "Chat picks the route");
        string id = (await target.GetAttributeAsync("data-recording-id"))!;
        ILocator item = page.Locator($"{LibraryHooks.Column} [role=listitem]", new() { Has = page.Locator($"a[data-recording-id='{id}']") });
        string column = (await page.Locator(LibraryHooks.Column).GetAttributeAsync("data-recordings-column"))!;
        int before = await page.Locator(LibraryHooks.Row).CountAsync();

        // Hidden until the row is hovered or focused, but always in the tab order.
        ILocator button = item.Locator("button[data-row-delete]");
        Assert.Equal("0", await button.EvaluateAsync<string>("b => getComputedStyle(b).opacity"));
        await target.FocusAsync();
        await Assertions.Expect(button).ToHaveCSSAsync("opacity", "1");
        await new LibraryJourneyTests(_app).AssertAxeCleanAsync(page, "library-row-delete");

        // The Delete key on the focused row opens the same dialog as the title block.
        await page.Keyboard.PressAsync("Delete");
        ILocator dialog = Feedback.Dialog(page, "Delete recording?");
        await dialog.WaitForAsync();
        Assert.Contains("Chat picks the route", await page.Locator("[data-delete-banner]").InnerTextAsync());
        await page.Locator("[data-delete-confirm]").ClickAsync();

        await Feedback.Snackbar(page, "Recording deleted").WaitForAsync();
        await page.Locator($"{LibraryHooks.Drawer} a[data-recording-id='{id}']").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        Assert.Equal(before - 1, await page.Locator(LibraryHooks.Row).CountAsync());
        // The drawer stays open on the same column, and focus lands on a remaining row.
        await Assertions.Expect(page.Locator($"{LibraryHooks.Drawer} [data-recordings-column='{column}']")).ToBeVisibleAsync();
        await page.WaitForFunctionAsync("() => document.activeElement?.matches('a[data-recording-id]')");
        Assert.Equal(
            (before - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            (await page.Locator($"{LibraryHooks.ChannelFor(column)} [data-channel-count]").InnerTextAsync()).Trim());
    }

    [Fact]
    public async Task A_live_recording_cannot_be_deleted()
    {
        await using Journey session = await OpenAsync(_app);
        IPage page = session.Page;

        ILocator growing = await LibraryHooks.RowAsync(page, LibraryHooks.Row, "Community day");
        await session.OpenRecordingAsync(growing);

        ILocator delete = await TitleBlock.MenuItemAsync(page, "data-delete");
        Assert.Equal("true", await delete.GetAttributeAsync("aria-disabled"));
        Assert.Contains("finishes", await delete.Locator("[data-delete-blocked]").TextContentAsync());
        await TitleBlock.CloseMenuAsync(page);
        await new LibraryJourneyTests(_app).AssertAxeCleanAsync(page, "live-actions-disabled");
    }
}
