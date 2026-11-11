using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

[Collection(MockAppCollection.Name)]
[Trait("Category", "E2E")]
public sealed class ExportJourneyTests(MockAppFixture app)
{
    [Fact]
    public async Task An_admin_exports_a_finished_recording_and_downloads_one_mp4()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator saved = page.Locator("nav#sk-drawer a.sk-row[data-state='saved']").First;
        string id = (await saved.GetAttributeAsync("data-recording-id"))!;
        await saved.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));

        ILocator export = page.Locator("button[data-export]");
        await export.WaitForAsync();
        await export.ClickAsync();

        // The row walks Queued/Running to MP4 ready; the button stays disabled while active.
        ILocator download = page.Locator("a[data-export-download]");
        await download.WaitForAsync(new() { Timeout = 60_000 });
        Assert.StartsWith("MP4 ready", (await page.Locator("[data-export-row] [role='status']").InnerTextAsync()).Trim());
        await page.Locator(".sk-snackbar", new() { HasTextString = "Export ready" }).WaitForAsync();
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "export-ready");

        IDownload file = await page.RunAndWaitForDownloadAsync(() => download.ClickAsync());
        Assert.EndsWith(".mp4", file.SuggestedFilename, StringComparison.Ordinal);
        string path = Path.Combine(app.ArtifactsDirectory, $"export-{Guid.NewGuid():N}.mp4");
        await file.SaveAsAsync(path);
        Assert.True(new FileInfo(path).Length > 100_000, "the MP4 has media in it");
        File.Delete(path);

        // The download leaves the page and its circuit alone: no reconnect, the card still reacts.
        await page.WaitForTimeoutAsync(1_000);
        Assert.False(await page.Locator(".sk-reconnect-modal, dialog[open]").IsVisibleAsync());
        await page.Locator("[data-copy-playlist]").ClickAsync();
        await page.Locator(".sk-snackbar", new() { HasTextString = "Playlist URL" }).WaitForAsync();

        // A reload rehydrates the row from the service (E-22).
        await page.ReloadAsync();
        await page.Locator("a[data-export-download]").WaitForAsync(new() { Timeout = 20_000 });
    }

    [Fact]
    public async Task A_running_export_can_be_cancelled_and_retried()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        // A different finished recording from the download journey, so the two never share a job.
        ILocator saved = page.Locator("nav#sk-drawer a.sk-row[data-state='saved']").Nth(1);
        string id = (await saved.GetAttributeAsync("data-recording-id"))!;
        await saved.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/r/{id}", StringComparison.Ordinal));

        await page.Locator("button[data-export]").ClickAsync();
        ILocator status = page.Locator("[data-export-row] [role='status']");
        await page.Locator("[data-export-row='Running'] [role='progressbar']").WaitForAsync(new() { Timeout = 30_000 });

        await page.Locator("button[data-export-cancel]").ClickAsync();

        await page.Locator("[data-export-row='Cancelled']").WaitForAsync();
        Assert.Equal("Export cancelled", (await status.InnerTextAsync()).Trim());
        Assert.Equal(0, await page.Locator(".sk-snackbar", new() { HasTextString = "Export" }).CountAsync());
        await new LibraryJourneyTests(app).AssertAxeCleanAsync(page, "export-cancelled");

        await page.Locator("button[data-export-retry]").ClickAsync();

        await page.Locator("a[data-export-download]").WaitForAsync(new() { Timeout = 60_000 });
        await page.Locator(".sk-snackbar", new() { HasTextString = "Export ready" }).WaitForAsync();
    }

    [Fact]
    public async Task A_live_recording_cannot_be_exported()
    {
        await using Session session = await OpenAsync(app.BaseUrl);
        IPage page = session.Page;
        ILocator growing = page.Locator("nav#sk-drawer a.sk-row", new() { HasTextString = "Community day" });
        await growing.ClickAsync();

        ILocator export = page.Locator("button[data-export]");
        await export.WaitForAsync();
        Assert.True(await export.IsDisabledAsync());
    }
}
