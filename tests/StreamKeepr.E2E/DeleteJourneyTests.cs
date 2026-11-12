using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

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
        await using Session session = await OpenAsync(_app.BaseUrl);
        IPage page = session.Page;

        string topId = (await page.Locator("nav#sk-drawer a.sk-row").First
            .GetAttributeAsync("data-recording-id"))!;
        ILocator row = page.Locator("nav#sk-drawer a.sk-row", new() { HasTextString = "Retro marathon" });
        string deletedId = (await row.GetAttributeAsync("data-recording-id"))!;
        Assert.NotEqual(topId, deletedId);

        await row.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{deletedId}", StringComparison.Ordinal));

        ILocator delete = page.Locator("button[data-delete]");
        await delete.WaitForAsync();
        if (await delete.IsDisabledAsync())
        {
            string reason = await page.Locator("[data-delete-blocked] .sk-tooltip-bubble")
                .TextContentAsync() ?? "";
            Assert.Fail($"Delete was disabled: {reason}");
        }

        // Cancel first: the dialog closes, focus returns to the trigger, and the recording stays.
        await delete.ClickAsync();
        ILocator dialog = page.Locator("[data-delete-dialog][open]");
        await dialog.WaitForAsync();
        Assert.Contains("Retro marathon", await page.Locator("[data-delete-banner]").InnerTextAsync());
        Assert.Contains("permanently", await page.Locator("[data-delete-banner]").InnerTextAsync());
        await page.Locator("[data-delete-cancel]").ClickAsync();
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        Assert.True(await delete.EvaluateAsync<bool>("b => b === document.activeElement"));
        Assert.Equal(1, await page.Locator($"a.sk-row[data-recording-id='{deletedId}']").CountAsync());

        // Confirm: the row disappears, the snackbar names the outcome, and Home opens the
        // first remaining recording in the current query.
        await delete.ClickAsync();
        await page.Locator("[data-delete-dialog][open]").WaitForAsync();
        await page.Locator("[data-delete-confirm]").ClickAsync();

        await page.Locator(".sk-snackbar", new() { HasTextString = "Recording deleted" }).WaitForAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{topId}", StringComparison.Ordinal));
        await page.Locator($"a.sk-row[data-recording-id='{deletedId}']")
            .WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await new LibraryJourneyTests(_app).AssertAxeCleanAsync(page, "delete");
    }

    [Fact]
    public async Task A_live_recording_cannot_be_deleted()
    {
        await using Session session = await OpenAsync(_app.BaseUrl);
        IPage page = session.Page;

        await page.Locator("nav#sk-drawer a.sk-row", new() { HasTextString = "Community day" }).ClickAsync();

        ILocator delete = page.Locator("button[data-delete]");
        await delete.WaitForAsync();
        Assert.True(await delete.IsDisabledAsync());
        Assert.Contains(
            "finishes",
            await page.Locator("[data-delete-blocked] .sk-tooltip-bubble").TextContentAsync());
    }
}
