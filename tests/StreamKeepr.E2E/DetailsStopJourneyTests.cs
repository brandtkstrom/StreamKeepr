using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

[Trait("Category", "E2E")]
public sealed class DetailsStopJourneyTests : IAsyncLifetime
{
    // Stopping the growing capture is destructive, so this class owns a disposable app.
    private readonly MockAppFixture _app = new("details-stop");

    public Task InitializeAsync() => _app.InitializeAsync();

    public Task DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task An_admin_stops_a_live_capture_from_its_details_card()
    {
        await using Session session = await OpenAsync(_app.BaseUrl);
        IPage page = session.Page;

        ILocator row = (await LibraryHooks.RowAsync(page, LibraryHooks.Row, "Community day"));
        string id = (await row.GetAttributeAsync("data-recording-id"))!;
        await row.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));

        ILocator stop = await TitleBlock.MenuItemAsync(page, "data-stop-recording");
        await stop.ClickAsync();

        ILocator dialog = Feedback.Dialog(page, new Regex("^Stop recording for "));
        await dialog.WaitForAsync();
        Assert.Contains("finalized", await dialog.InnerTextAsync());
        Assert.Contains("stays paused", await dialog.InnerTextAsync());
        await new LibraryJourneyTests(_app).AssertAxeCleanAsync(page, "stop-dialog");
        await page.Locator("[data-stop-confirm]").ClickAsync();

        await Feedback.Snackbar(page, "Channel paused").WaitForAsync();
        // Once the capture is finalized the live chip goes, and More actions no longer offers
        // Stop recording.
        await page.Locator("[data-details-actions] [data-status]").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await TitleBlock.MenuItemAsync(page, "data-delete");
        Assert.Equal(0, await page.Locator("[role='menuitem'][data-stop-recording]").CountAsync());
        await TitleBlock.CloseMenuAsync(page);

        await page.GotoAsync(_app.BaseUrl);
        await LibraryHooks.OpenLibraryAsync(page);
        await page.Locator(LibraryHooks.Channel).First.WaitForAsync();
        Assert.Contains(
            "Paused",
            await page.Locator(LibraryHooks.ChannelFor("mock_rain") + " [data-channel-meta]").TextContentAsync());
        Assert.Equal(
            0,
            await page.Locator(LibraryHooks.ChannelFor("mock_rain") + " [data-live-ring]").CountAsync());
        await new LibraryJourneyTests(_app).AssertAxeCleanAsync(page, "paused");
    }
}
