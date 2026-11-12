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

        ILocator row = page.Locator("nav#sk-drawer a.sk-row", new() { HasTextString = "Community day" });
        string id = (await row.GetAttributeAsync("data-recording-id"))!;
        await row.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));

        ILocator stop = page.Locator("button[data-stop-recording]");
        await stop.WaitForAsync();
        await stop.ClickAsync();

        ILocator dialog = page.Locator("[data-stop-dialog][open]");
        await dialog.WaitForAsync();
        Assert.Contains("finalized", await dialog.InnerTextAsync());
        Assert.Contains("stays paused", await dialog.InnerTextAsync());
        await page.Locator("[data-stop-confirm]").ClickAsync();

        await page.Locator(".sk-snackbar", new() { HasTextString = "Channel paused" }).WaitForAsync();
        await page.WaitForSelectorAsync("[data-stop-recording]", new() { State = WaitForSelectorState.Hidden });

        await page.GotoAsync(_app.BaseUrl);
        await page.Locator("nav#sk-drawer .sk-group").First.WaitForAsync();
        Assert.Contains(
            "Paused",
            await page.Locator("[data-tree-key='g:mock_rain'] .meta").TextContentAsync());
        Assert.Equal(
            0,
            await page.Locator("[data-tree-key='g:mock_rain'] .sk-live-ring").CountAsync());
    }
}
