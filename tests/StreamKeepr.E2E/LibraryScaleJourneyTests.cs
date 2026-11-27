using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

/// <summary>
/// Measures the all-DOM library (E-19) near the contract's ~2,000 flattened items (#110) and
/// writes the numbers to <c>library-scale.txt</c>. The budgets sit well above the measured
/// values: they catch a regression that would justify virtualization, not normal CI jitter.
/// The test runs alone, after the parallel classes, so it measures the library rather than
/// other test apps competing for the runner's CPU.
/// </summary>
[CaptureFailure]
[Trait("Category", "E2E")]
[Collection(LibraryScaleCollection.Name)]
public sealed class LibraryScaleJourneyTests : IAsyncLifetime
{
    private const int ArchiveCount = 1000;
    private const int SeededCount = 8;
    private const int ScrollSteps = 60;
    private const int WarmLoads = 3;
    // Each channel's column holds its share of the archive (the seed spreads it over the mock
    // channels), so the longest column is at least this many rows.
    private const int MinColumnRows = 200;

    private static readonly TimeSpan ColdFirstByteBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LoadBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ViewChangeBudget = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FrameBudget = TimeSpan.FromMilliseconds(250);

    // A string, not LibraryJourneyTests.Rows(page), because the waits run it inside the page.
    private const string RowSelector = LibraryHooks.Row;
    private const string Search = "input[data-search-input]";

    // The scale seed has its own app so the shared fixture's journeys keep their eight
    // recordings. The live feed is off so nothing re-renders the drawer mid-measurement.
    private readonly MockAppFixture _app = new(
        "library-scale",
        new Dictionary<string, string>
        {
            ["Mock__Seed__ScaleRecordings"] = ArchiveCount.ToString(CultureInfo.InvariantCulture),
            ["Mock__Feed__Enabled"] = "false",
        });

    public Task InitializeAsync() => _app.InitializeAsync();

    public Task DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task A_two_thousand_item_library_loads_searches_sorts_and_scrolls_within_budget()
    {
        const int totalRows = ArchiveCount + SeededCount;
        await using Journey session = await OpenAsync(_app);
        IPage page = session.Page;

        // Cold: the app's first page, in a browser that has never loaded it.
        // A runner with no auto-detect proxy passes this budget with or without --no-proxy-server,
        // so in CI it guards server warm-up, not the WPAD wait (see E2EBrowser).
        double coldFirstByteMs = await FirstByteMsAsync(page);

        // Warm: reload until every channel's count is in and the circuit is interactive. The
        // channels are prerendered, so this mostly measures HTML size and circuit start.
        var warm = new List<(double FirstByte, double Ready)>();
        for (int i = 0; i < WarmLoads; i++)
        {
            await session.ReloadAsync();
            await page.Locator(LibraryHooks.ShellReady).WaitForAsync();
            // The counts are in the open library (Md and up it starts as the rail).
            await LibraryHooks.OpenLibraryAsync(page);
            await WaitForRowsAsync(page, totalRows);
            warm.Add((await FirstByteMsAsync(page), await page.EvaluateAsync<double>("() => performance.now()")));
        }

        double warmFirstByteMs = Median(warm.Select(load => load.FirstByte));
        double warmReadyMs = Median(warm.Select(load => load.Ready));
        int htmlKb = await page.EvaluateAsync<int>(
            "() => Math.round(performance.getEntriesByType('navigation')[0].decodedBodySize / 1024)");
        // The longest column: the channel with the most recordings.
        string longest = await page.EvaluateAsync<string>("""
            () => [...document.querySelectorAll('nav[aria-label=Library] [data-library-channel]')]
              .map(o => [o.dataset.libraryChannel, +o.querySelector('[data-channel-count]').textContent])
              .sort((a, b) => b[1] - a[1])[0][0]
            """);
        double openMs = await TimeUntilAsync(
            page,
            () => LibraryHooks.OpenChannelAsync(page, longest),
            "() => document.querySelectorAll('[data-recordings-column] a[data-recording-id]').length > 0",
            null);
        int columnRows = await page.Locator(RowSelector).CountAsync();
        int flattened = await page.Locator(
            $"{LibraryHooks.Drawer} :is([data-library-channel], [data-day-header], a[data-recording-id])").CountAsync();
        int capturing = (await LibraryJourneyTests.RowStatesAsync(page)).Count(state => state is "rec" or "reconnecting");
        Assert.InRange(capturing, 1, SeededCount);
        await LibraryHooks.OpenChannelAsync(page, longest);

        // Search: the first keystroke until one row is left. Includes the 250 ms debounce.
        double searchMs = await TimeUntilRowsAsync(
            page, () => page.Locator(Search).FillAsync("Archive stream 777"), 1);

        // Clear: Esc until every row is back. This re-renders the whole list.
        double clearMs = await TimeUntilRowsAsync(
            page, () => page.Locator(Search).PressAsync("Escape"), totalRows);

        // State filter: Saved hides the live captures; All brings them back. Startup recovery
        // decides which seeded sessions are still capturing, so they were counted above.
        double filterMs = await TimeUntilRowsAsync(
            page, () => page.Locator("[data-filter='saved']").ClickAsync(),
            totalRows - capturing);
        await TimeUntilRowsAsync(
            page, () => page.Locator("[data-filter='all']").ClickAsync(), totalRows);

        // Sort: Oldest first reorders the open column, timed until its first row changes.
        string? firstRow = await page.Locator(RowSelector).First.GetAttributeAsync("data-recording-id");
        double sortMs = await TimeUntilAsync(
            page,
            async () =>
            {
                await page.Locator("[data-sort]").ClickAsync();
                await page.GetByRole(AriaRole.Menuitem, new() { Name = "Oldest first" }).ClickAsync();
            },
            $"id => document.querySelector('{RowSelector}')?.dataset.recordingId !== id",
            firstRow);

        // Scroll: step the drawer from top to bottom one frame at a time.
        JsonElement scroll = await page.EvaluateAsync<JsonElement>("""
            async steps => {
              const list = document.querySelector('[data-column-body]');
              const frame = () => new Promise(requestAnimationFrame);
              const deltas = [];
              list.scrollTop = 0;
              await frame();
              let last = performance.now();
              for (let i = 1; i <= steps; i++) {
                list.scrollTop = (list.scrollHeight - list.clientHeight) * i / steps;
                await frame();
                const now = performance.now();
                deltas.push(now - last);
                last = now;
              }
              deltas.sort((a, b) => a - b);
              return {
                height: list.scrollHeight,
                max: deltas[deltas.length - 1],
                p95: deltas[Math.ceil(deltas.length * 0.95) - 1],
                over50: deltas.filter(d => d > 50).length,
                atBottom: Math.abs(list.scrollTop + list.clientHeight - list.scrollHeight) < 2,
              };
            }
            """, ScrollSteps);
        double maxFrameMs = scroll.GetProperty("max").GetDouble();

        await File.WriteAllTextAsync(
            Path.Combine(_app.ArtifactsDirectory, "library-scale.txt"),
            string.Join(
                Environment.NewLine,
                $"rows                {totalRows:N0}",
                $"longest column      {columnRows:N0} rows ({flattened:N0} with channels and day headers), opened in {openMs:N0} ms",
                $"cold first byte     {coldFirstByteMs,8:N0} ms (budget {ColdFirstByteBudget.TotalMilliseconds:N0}; first page after start)",
                $"warm first byte     {warmFirstByteMs,8:N0} ms (median of 3 reloads)",
                $"warm ready          {warmReadyMs,8:N0} ms (budget {LoadBudget.TotalMilliseconds:N0}; all rows and the circuit)",
                $"page HTML           {htmlKb,8:N0} KB",
                $"search to 1 row     {searchMs,8:N0} ms (budget {SearchBudget.TotalMilliseconds:N0}, includes 250 ms debounce)",
                $"clear to all rows   {clearMs,8:N0} ms (budget {ViewChangeBudget.TotalMilliseconds:N0})",
                $"filter to Saved     {filterMs,8:N0} ms (budget {ViewChangeBudget.TotalMilliseconds:N0})",
                $"sort Oldest first   {sortMs,8:N0} ms (budget {ViewChangeBudget.TotalMilliseconds:N0}, includes opening the menu)",
                $"scroll height       {scroll.GetProperty("height").GetInt32():N0} px",
                $"scroll frame max    {maxFrameMs,8:N1} ms (budget {FrameBudget.TotalMilliseconds:N0})",
                $"scroll frame p95    {scroll.GetProperty("p95").GetDouble(),8:N1} ms",
                $"frames over 50 ms   {scroll.GetProperty("over50").GetInt32()} of {ScrollSteps}"));

        Assert.True(columnRows >= MinColumnRows, $"only {columnRows} rows in the longest column");
        Assert.True(openMs <= ViewChangeBudget.TotalMilliseconds, $"opening the longest column took {openMs:N0} ms");
        Assert.True(scroll.GetProperty("atBottom").GetBoolean(), "the column did not reach the bottom");
        Assert.True(
            coldFirstByteMs <= ColdFirstByteBudget.TotalMilliseconds,
            $"the first page took {coldFirstByteMs:N0} ms to its first byte");
        Assert.True(warmReadyMs <= LoadBudget.TotalMilliseconds, $"a warm load took {warmReadyMs:N0} ms");
        Assert.True(searchMs <= SearchBudget.TotalMilliseconds, $"search took {searchMs:N0} ms");
        Assert.True(clearMs <= ViewChangeBudget.TotalMilliseconds, $"clear took {clearMs:N0} ms");
        Assert.True(filterMs <= ViewChangeBudget.TotalMilliseconds, $"the Saved filter took {filterMs:N0} ms");
        Assert.True(sortMs <= ViewChangeBudget.TotalMilliseconds, $"sorting took {sortMs:N0} ms");
        Assert.True(maxFrameMs <= FrameBudget.TotalMilliseconds, $"a scroll frame took {maxFrameMs:N1} ms");
    }

    private static Task<double> FirstByteMsAsync(IPage page) =>
        page.EvaluateAsync<double>("() => performance.getEntriesByType('navigation')[0].responseStart");

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    // The recordings matching the view: the sum of the channels' counts.
    private static string RowCountIs =>
        "n => [...document.querySelectorAll('nav[aria-label=Library] [data-channel-count]')]" +
        ".reduce((sum, c) => sum + +c.textContent, 0) === n";

    private static Task WaitForRowsAsync(IPage page, int rows) =>
        page.WaitForFunctionAsync(RowCountIs, rows, new() { Timeout = 30_000, PollingInterval = 10 });

    private static Task<double> TimeUntilRowsAsync(IPage page, Func<Task> action, int rows) =>
        TimeUntilAsync(page, action, RowCountIs, rows);

    /// <summary>
    /// Page time from just before <paramref name="action"/> until <paramref name="predicate"/>
    /// holds. Includes the Playwright round trips, so results err slow.
    /// </summary>
    private static async Task<double> TimeUntilAsync(
        IPage page, Func<Task> action, string predicate, object? arg)
    {
        double start = await page.EvaluateAsync<double>("() => performance.now()");
        await action();
        await page.WaitForFunctionAsync(predicate, arg, new() { Timeout = 30_000, PollingInterval = 10 });
        return await page.EvaluateAsync<double>("() => performance.now()") - start;
    }
}

/// <summary>
/// Runs <see cref="LibraryScaleJourneyTests"/> with no other test class in parallel. On CI,
/// a warm load measured 0.9–1.6 s while other classes ran beside it, and once 2.4 s
/// (run 43210166704), against about 0.3 s locally.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LibraryScaleCollection
{
    public const string Name = "LibraryScale";
}
