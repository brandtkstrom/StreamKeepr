using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;
using static StreamKeepr.E2E.LibraryJourneyTests;

namespace StreamKeepr.E2E;

/// <summary>
/// Measures the all-DOM library (E-19) near the contract's ~2,000 flattened items (#110) and
/// writes the numbers to <c>library-scale.txt</c>. The budgets sit well above the measured
/// values: they catch a regression that would justify virtualization, not normal CI jitter.
/// </summary>
[Trait("Category", "E2E")]
public sealed class LibraryScaleJourneyTests : IAsyncLifetime
{
    private const int ArchiveRecordings = 1000;
    private const int SeededRecordings = 8;
    private const int MinFlattenedItems = 2000;

    private static readonly TimeSpan LoadBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ClearBudget = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FrameBudget = TimeSpan.FromMilliseconds(250);

    private const string Rows = "nav#sk-drawer a.sk-row";
    private const string Search = "input[data-search-input]";

    // The scale seed has its own app so the shared fixture's journeys keep their eight
    // recordings. The live feed is off so nothing re-renders the drawer mid-measurement.
    private readonly MockAppFixture _app = new(
        "library-scale",
        new Dictionary<string, string>
        {
            ["Mock__Seed__ScaleRecordings"] = ArchiveRecordings.ToString(CultureInfo.InvariantCulture),
            ["Mock__Feed__Enabled"] = "false",
        });

    public Task InitializeAsync() => _app.InitializeAsync();

    public Task DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task A_two_thousand_item_library_loads_searches_and_scrolls_within_budget()
    {
        const int totalRows = ArchiveRecordings + SeededRecordings;
        await using Session session = await OpenAsync(_app.BaseUrl);
        IPage page = session.Page;

        // Cold: the app's first page. Its server time is warm-up (about 6 s locally with 8 or
        // 1,008 recordings alike), so it is reported, not budgeted.
        double coldFirstByteMs = await FirstByteMsAsync(page);

        // Warm: reload until every row is in the DOM and the circuit is interactive. Median of 3.
        var warm = new List<(double FirstByte, double Ready)>();
        for (int i = 0; i < 3; i++)
        {
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.Locator($"{Search}[data-sk-ready]").WaitForAsync();
            await page.WaitForFunctionAsync(
                $"n => document.querySelectorAll('{Rows}').length === n", totalRows);
            warm.Add((await FirstByteMsAsync(page), await page.EvaluateAsync<double>("() => performance.now()")));
        }

        double warmFirstByteMs = warm.Select(load => load.FirstByte).Order().ElementAt(1);
        double warmReadyMs = warm.Select(load => load.Ready).Order().ElementAt(1);
        int htmlKb = await page.EvaluateAsync<int>(
            "() => Math.round(performance.getEntriesByType('navigation')[0].decodedBodySize / 1024)");
        int flattened = await page.Locator(
            "nav#sk-drawer :is(.sk-group, .sk-day, .sk-row)").CountAsync();

        // Search: the first keystroke until one row is left. Includes the 250 ms debounce.
        double searchMs = await TimeUntilRowsAsync(
            page, () => page.Locator(Search).FillAsync("Archive stream 777"), 1);

        // Clear: Esc until every row is back.
        double clearMs = await TimeUntilRowsAsync(
            page, () => page.Locator(Search).PressAsync("Escape"), totalRows);

        // Scroll: step the drawer from top to bottom one frame at a time.
        JsonElement scroll = await page.EvaluateAsync<JsonElement>("""
            async () => {
              const list = document.querySelector('nav#sk-drawer .sk-drawer-list');
              const frame = () => new Promise(requestAnimationFrame);
              const steps = 60;
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
                p95: deltas[Math.floor(deltas.length * 0.95) - 1],
                over50: deltas.filter(d => d > 50).length,
                atBottom: Math.abs(list.scrollTop + list.clientHeight - list.scrollHeight) < 2,
              };
            }
            """);
        double maxFrameMs = scroll.GetProperty("max").GetDouble();

        await File.WriteAllTextAsync(
            Path.Combine(_app.ArtifactsDirectory, "library-scale.txt"),
            string.Join(
                Environment.NewLine,
                $"rows                {totalRows:N0}",
                $"flattened items     {flattened:N0} (groups + day headers + rows)",
                $"cold first byte     {coldFirstByteMs,8:N0} ms (first page after start; not budgeted)",
                $"warm first byte     {warmFirstByteMs,8:N0} ms (median of 3 reloads)",
                $"warm ready          {warmReadyMs,8:N0} ms (budget {LoadBudget.TotalMilliseconds:N0}; all rows and the circuit)",
                $"page HTML           {htmlKb,8:N0} KB",
                $"search to 1 row     {searchMs,8:N0} ms (budget {SearchBudget.TotalMilliseconds:N0}, includes 250 ms debounce)",
                $"clear to all rows   {clearMs,8:N0} ms (budget {ClearBudget.TotalMilliseconds:N0})",
                $"scroll height       {scroll.GetProperty("height").GetInt32():N0} px",
                $"scroll frame max    {maxFrameMs,8:N1} ms (budget {FrameBudget.TotalMilliseconds:N0})",
                $"scroll frame p95    {scroll.GetProperty("p95").GetDouble(),8:N1} ms",
                $"frames over 50 ms   {scroll.GetProperty("over50").GetInt32()} of 60"));

        Assert.True(flattened >= MinFlattenedItems, $"only {flattened} flattened items rendered");
        Assert.True(scroll.GetProperty("atBottom").GetBoolean(), "the drawer did not reach the bottom");
        Assert.True(warmReadyMs <= LoadBudget.TotalMilliseconds, $"a warm load took {warmReadyMs:N0} ms");
        Assert.True(searchMs <= SearchBudget.TotalMilliseconds, $"search took {searchMs:N0} ms");
        Assert.True(clearMs <= ClearBudget.TotalMilliseconds, $"clear took {clearMs:N0} ms");
        Assert.True(maxFrameMs <= FrameBudget.TotalMilliseconds, $"a scroll frame took {maxFrameMs:N1} ms");
    }

    private static Task<double> FirstByteMsAsync(IPage page) =>
        page.EvaluateAsync<double>("() => performance.getEntriesByType('navigation')[0].responseStart");

    private static async Task<double> TimeUntilRowsAsync(IPage page, Func<Task> action, int rows)
    {
        double start = await page.EvaluateAsync<double>("() => performance.now()");
        await action();
        await page.WaitForFunctionAsync(
            $"n => document.querySelectorAll('{Rows}').length === n",
            rows,
            new() { Timeout = 30_000, PollingInterval = 10 });
        return await page.EvaluateAsync<double>("() => performance.now()") - start;
    }
}
