using Microsoft.Playwright;

namespace StreamKeepr.E2E;

/// <summary>
/// How much of the shell a navigation must make interactive before the journey may act.
/// </summary>
internal enum JourneyReady
{
    /// <summary>Do not wait: the page deliberately runs without the Blazor circuit.</summary>
    None,

    /// <summary>The shell or the sign-in page is interactive.</summary>
    Shell,

    /// <summary>The shell is interactive and the library drawer is rendered.</summary>
    Library,
}

/// <summary>
/// One browser session for one test. It owns its context and page on the process's shared Chrome
/// (<see cref="E2EBrowser.SharedAsync"/>), starts a trace for every test, and stops it to the artifacts directory only when the
/// test failed (<see cref="FailureCapture"/>). It also owns the readiness and reconnect rules
/// from <c>docs/testing-contract.md</c>: a navigation does not return until the destination is
/// interactive, and an action waits out a transient reconnect instead of dropping into a
/// circuit that cannot receive it.
/// </summary>
internal sealed class Journey : IAsyncDisposable
{
    /// <summary>The recording page's interactive marker: the seek bar is wired and playable.</summary>
    private const string RecordingReady = "[data-sk='seek'][aria-disabled='false']";

    /// <summary>Mirrors <c>COLUMN_WAIT_MS</c> in <c>library-keys.js</c>.</summary>
    private const int FocusHandoffTimeoutMs = 5_000;

    /// <summary>The reserved host the Mock badge catalog points its images at.</summary>
    public const string MockBadgeImageHost = "https://badges.mock.invalid/";

    // A 16x16 SVG served for every Mock badge image; it only needs to decode at the 18px slot.
    private const string MockBadgeImage =
        "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'><rect width='16' height='16' fill='#9146ff'/></svg>";

    private readonly MockAppFixture _app;
    private readonly FailureCapture? _failure;
    private bool _disposed;

    private Journey(
        MockAppFixture app,
        IBrowserContext context,
        IPage page,
        FailureCapture? failure)
    {
        _app = app;
        _failure = failure;
        Context = context;
        Page = page;
    }

    public IBrowserContext Context { get; }

    public IPage Page { get; }

    /// <summary>
    /// Opens a session. With <paramref name="url"/> it navigates and waits for the requested
    /// readiness; without one it returns a blank page, for callers that must install a route or
    /// init script before the first request.
    /// </summary>
    public static async Task<Journey> OpenAsync(
        MockAppFixture app,
        string? url = null,
        ViewportSize? viewport = null,
        string? initScript = null,
        bool hasTouch = false,
        JourneyReady ready = JourneyReady.Library)
    {
        IBrowserContext? context = null;
        Journey? journey = null;
        try
        {
            IBrowser browser = await E2EBrowser.SharedAsync();
            context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = viewport ?? new ViewportSize { Width = 1280, Height = 820 },
                HasTouch = hasTouch,
            });
            if (initScript is not null)
                await context.AddInitScriptAsync(initScript);

            await context.Tracing.StartAsync(new TracingStartOptions
            {
                Screenshots = true,
                Snapshots = true,
                Sources = true,
            });

            // The Mock badge source points at the reserved .invalid host. Fulfil it for every
            // context so no test run depends on DNS and every badge image can decode.
            await context.RouteAsync($"{MockBadgeImageHost}**", async route =>
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "image/svg+xml",
                    Body = MockBadgeImage,
                });
            });

            IPage page = await context.NewPageAsync();
            journey = new Journey(app, context, page, FailureCapture.Current);
            if (url is not null)
                await journey.GotoAsync(url, ready);
            return journey;
        }
        catch
        {
            if (journey is not null)
                await journey.DisposeAsync();
            else if (context is not null)
                await context.DisposeAsync();

            throw;
        }
    }

    /// <summary>Navigates and waits until the destination can take input.</summary>
    public async Task GotoAsync(string url, JourneyReady ready = JourneyReady.Library)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        await WaitForReconnectAsync();
        await Page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await WaitForReadyAsync(ready);
    }

    /// <summary>Reloads and waits until the destination can take input.</summary>
    public async Task ReloadAsync(JourneyReady ready = JourneyReady.Library)
    {
        await WaitForReconnectAsync();
        await Page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await WaitForReadyAsync(ready);
    }

    /// <summary>
    /// Waits for the shell's interactive marker, or the sign-in page (which has no circuit by
    /// design), then for the library drawer when requested.
    /// </summary>
    public async Task WaitForReadyAsync(JourneyReady ready = JourneyReady.Library, int timeoutMs = 20_000)
    {
        if (ready == JourneyReady.None)
            return;

        try
        {
            await WaitForShellAsync(Page, timeoutMs);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            throw new InvalidOperationException(
                $"The shell did not become interactive.\n{await DescribeStateAsync()}\n{AppLogs(20)}", ex);
        }

        if (ready == JourneyReady.Library)
        {
            await Page.Locator("nav[aria-label=Library]").Last.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = timeoutMs });
        }
    }

    /// <summary>
    /// Waits for the shell's interactive marker, or the sign-in page (which has no circuit by
    /// design). A hard reconnect state ends the wait at once with the URL, reconnect state and
    /// focused element instead of timing out on a marker that cannot appear.
    /// </summary>
    public static async Task WaitForShellAsync(IPage page, int timeoutMs = 20_000)
    {
        ArgumentNullException.ThrowIfNull(page);
        await page.WaitForFunctionAsync(
            """
            () => {
              if (document.querySelector("input[data-search-input][data-sk-ready]")) return true;
              if (document.querySelector("[data-signin]")) return true;
              const reconnect = document.body?.dataset.skReconnect;
              return reconnect === "failed" || reconnect === "paused" || reconnect === "resume-failed";
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = timeoutMs });

        string? reconnect = await page.EvaluateAsync<string?>(
            "() => document.body?.dataset.skReconnect ?? null");
        if (reconnect is "failed" or "paused" or "resume-failed")
        {
            throw new InvalidOperationException(
                $"The page is in the hard reconnect state '{reconnect}'.\n{await DescribeStateAsync(page)}");
        }

        await WaitForReconnectAsync(page, timeoutMs);
    }

    /// <summary>
    /// Waits for the shell marker after a client-side navigation, without touching the URL.
    /// </summary>
    public Task WaitForShellAsync(int timeoutMs = 20_000) =>
        WaitForReadyAsync(JourneyReady.Shell, timeoutMs);

    /// <summary>Waits until the recording page's seek bar is wired and playable.</summary>
    public async Task WaitForRecordingAsync(int timeoutMs = 30_000)
    {
        try
        {
            await Page.WaitForFunctionAsync(
                """
                () => {
                  if (document.querySelector("[data-sk='seek'][aria-disabled='false']")) return true;
                  const reconnect = document.body?.dataset.skReconnect;
                  return reconnect === "failed" || reconnect === "paused" || reconnect === "resume-failed";
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = timeoutMs });
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                "The recording page did not become interactive.\n" +
                $"{await DescribeStateAsync()}\n{AppLogs(20)}",
                ex);
        }

        if (await Page.Locator(RecordingReady).CountAsync() == 0)
        {
            throw new InvalidOperationException(
                "The recording page did not become interactive.\n" +
                $"{await DescribeStateAsync()}\n{AppLogs(20)}");
        }

        await WaitForReconnectAsync();
    }

    /// <summary>
    /// Clicks a row and waits for its recording page, so the next press cannot land on the
    /// server-rendered page before the circuit is interactive.
    /// </summary>
    public async Task OpenRecordingAsync(ILocator row, int timeoutMs = 30_000)
    {
        string id = await row.GetAttributeAsync("data-recording-id")
            ?? throw new InvalidOperationException("The recording row has no data-recording-id.");
        await ActAsync(() => row.ClickAsync());
        await WaitForUrlAsync(
            Page, url => new Uri(url).AbsolutePath == $"/r/{id}", timeoutMs);
        await WaitForRecordingAsync(timeoutMs);
    }

    /// <summary>
    /// Waits out a transient reconnect before acting. A hard loss (the modal states) fails the
    /// action at once with the URL, reconnect state, and focused element, instead of waiting for
    /// a Playwright timeout that hides the cause.
    /// </summary>
    public async Task WaitForReconnectAsync(int timeoutMs = 10_000)
    {
        try
        {
            await WaitForReconnectAsync(Page, timeoutMs);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                $"The page did not leave the reconnect state before an action.\n" +
                $"{await DescribeStateAsync()}\n{AppLogs(20)}",
                ex);
        }
    }

    /// <summary>
    /// Waits until the address bar matches <paramref name="predicate"/>. Polling the current URL
    /// is deliberate: a click whose navigation finishes before <see cref="IPage.WaitForURLAsync"/>
    /// starts would otherwise wait for a future navigation that never comes (the #296 sign-in
    /// race), while the action itself had already succeeded.
    /// </summary>
    public static async Task WaitForUrlAsync(
        IPage page, Func<string, bool> predicate, int timeoutMs = 30_000)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(predicate);

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            string url = page.Url;
            if (predicate(url))
                return;

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"The page did not reach the expected URL within {timeoutMs:N0} ms. " +
                    $"Current URL: {url}");
            }

            await Task.Delay(50);
        }
    }

    /// <summary>Waits out a transient reconnect on any page.</summary>
    public static async Task WaitForReconnectAsync(IPage page, int timeoutMs = 10_000)
    {
        ArgumentNullException.ThrowIfNull(page);
        await page.WaitForFunctionAsync(
            """
            () => {
              const state = document.body?.dataset.skReconnect;
              return !state || state === "hide";
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = timeoutMs });
    }

    /// <summary>Runs an action after the reconnect guard.</summary>
    public async Task ActAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await WaitForReconnectAsync();
        await action();
    }

    /// <summary>
    /// Opens the focused channel's column from the keyboard. The column's focus handoff waits
    /// five seconds by design; if the render is later, focus stays on the channel, so the
    /// journey presses Right again rather than hanging on the default 30 s timeout.
    /// </summary>
    public async Task OpenFocusedChannelAsync(int attempts = 3, int columnTimeoutMs = 10_000)
    {
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            string? login = await Page.EvaluateAsync<string?>(
                "() => document.activeElement?.getAttribute('data-library-channel')");
            if (string.IsNullOrEmpty(login))
            {
                throw new InvalidOperationException(
                    $"The focused element is not a library channel.\n{await DescribeStateAsync()}");
            }

            await ActAsync(() => Page.Keyboard.PressAsync("ArrowRight"));
            await Page.Locator($"{LibraryHooks.Drawer} [data-recordings-column='{login}']")
                .WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = columnTimeoutMs,
                });

            DateTimeOffset focusDeadline = DateTimeOffset.UtcNow.AddMilliseconds(FocusHandoffTimeoutMs);
            while (DateTimeOffset.UtcNow < focusDeadline)
            {
                if (await Page.EvaluateAsync<bool>(
                        "() => document.activeElement?.matches('[data-recordings-column] a[data-recording-id]') === true"))
                {
                    return;
                }

                await Task.Delay(50);
            }

            // The column exists but focus stayed on the channel: press Right again while focus
            // still belongs to the channel. If focus moved into the column, the loop above
            // returned before this point.
        }

        throw new InvalidOperationException(
            $"The column did not take focus after {attempts} Right presses.\n{await DescribeStateAsync()}");
    }

    /// <summary>URL, reconnect state and focused element, for a fast failure message.</summary>
    public Task<string> DescribeStateAsync() => DescribeStateAsync(Page);

    private string AppLogs(int count) =>
        $"Mock app log (last {count} lines):{Environment.NewLine}" +
        string.Join(Environment.NewLine, _app.RecentLogs(count));

    /// <summary>URL, reconnect state and focused element for any page.</summary>
    public static async Task<string> DescribeStateAsync(IPage page)
    {
        try
        {
            string state = await page.EvaluateAsync<string>(
                """
                () => JSON.stringify({
                  url: location.href,
                  reconnect: document.body?.dataset.skReconnect ?? null,
                  ready: !!document.querySelector("input[data-search-input][data-sk-ready]"),
                  active: document.activeElement ? {
                    tag: document.activeElement.tagName,
                    id: document.activeElement.id || null,
                    hook: document.activeElement.getAttribute("data-sk") || null,
                    text: (document.activeElement.innerText || document.activeElement.value || "")
                      .trim().replace(/\s+/g, " ").slice(0, 120)
                  } : null
                }, null, 2)
                """);
            return $"Browser state:\n{state}";
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            return $"Browser state unavailable: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        bool failed = _failure?.Failed == true;
        string? tracePath = failed
            ? Path.Combine(_app.ArtifactsDirectory, $"{_failure!.FileStem}.trace.zip")
            : null;

        string? traceError = null;
        try
        {
            try
            {
                await Context.Tracing.StopAsync(new TracingStopOptions { Path = tracePath });
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                // Best effort: the browser may have crashed or the context already closed.
                traceError = ex.Message;
            }

            if (failed)
                await WriteFailureArtifactsAsync(tracePath, traceError);
        }
        finally
        {
            // Always release the context, even if reporting failed. The shared Chrome stays up.
            await Context.DisposeAsync();
        }
    }

    private async Task WriteFailureArtifactsAsync(string? tracePath, string? traceError)
    {
        try
        {
            Directory.CreateDirectory(_app.ArtifactsDirectory);
            string state = await DescribeStateAsync();
            string logs = string.Join(Environment.NewLine, _app.RecentLogs(80));
            string report =
                $"{state}{Environment.NewLine}{Environment.NewLine}" +
                $"Trace: {tracePath ?? "(not written)"} {traceError}{Environment.NewLine}{Environment.NewLine}" +
                $"Mock app log (last 80 lines):{Environment.NewLine}{logs}";
            await File.WriteAllTextAsync(
                Path.Combine(_app.ArtifactsDirectory, $"{_failure!.FileStem}.failure.txt"),
                report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failure reporting must never mask the test's own failure.
        }
    }
}
