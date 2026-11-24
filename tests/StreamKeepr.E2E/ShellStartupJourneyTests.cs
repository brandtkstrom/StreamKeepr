using Microsoft.Playwright;

namespace StreamKeepr.E2E;

/// <summary>
/// The first slice of #296: the library's open button is server-rendered, so a click sent
/// before the Blazor circuit is interactive is dropped and the test waits forever for a drawer
/// that never opens. Delaying <c>/_blazor/negotiate</c> makes that window deterministic.
/// </summary>
[Collection(MockAppCollection.Name)]
[CaptureFailure]
[Trait("Category", "E2E")]
public sealed class ShellStartupJourneyTests(MockAppFixture app)
{
    [Fact]
    public async Task Opening_the_library_waits_for_the_circuit_after_a_slow_negotiate()
    {
        await using Journey session = await Journey.OpenAsync(app, url: null, ready: JourneyReady.None);
        IPage page = session.Page;

        var negotiateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var negotiateFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/_blazor/negotiate*", async route =>
        {
            negotiateStarted.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(3));
            await route.ContinueAsync();
            negotiateFinished.TrySetResult();
        });

        // Goto with None deliberately does not wait: this is the old journey flow, which
        // reached for the library while the circuit was still negotiating.
        await session.GotoAsync(app.BaseUrl, JourneyReady.None);

        // The request is in flight, but the three-second delay has not elapsed.
        await negotiateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(negotiateFinished.Task.IsCompleted,
            "the delayed negotiate finished before the library was opened; the window closed");
        Assert.Equal(0, await page.Locator("[data-shell][data-library-open]").CountAsync());

        await LibraryHooks.OpenLibraryAsync(page);

        await Assertions.Expect(page.Locator("[data-shell][data-library-open]")).ToBeAttachedAsync();
        Assert.True(await page.Locator(LibraryHooks.Drawer).IsVisibleAsync());
    }

}
