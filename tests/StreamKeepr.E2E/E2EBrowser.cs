using Microsoft.Playwright;

namespace StreamKeepr.E2E;

/// <summary>
/// How every journey gets Chrome. It is branded Chrome because Playwright's bundled
/// Chromium lacks H.264 (E-21). The suite only talks to the Mock app on loopback, so the
/// browser skips the system proxy settings. Without that, a machine set to auto-detect a proxy
/// (WPAD/PAC) waits about 6 s on every fresh browser's first request before it connects.
///
/// The test process shares one Playwright driver and one Chrome; every test still opens its own
/// <see cref="IBrowserContext"/>, which isolates cookies, storage, and permissions, and must
/// dispose it. Never close the shared browser from a test.
/// </summary>
internal static class E2EBrowser
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;

    static E2EBrowser()
    {
        // Disposing the driver connection ends the driver, which closes Chrome with it.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _playwright?.Dispose();
    }

    private static BrowserTypeLaunchOptions LaunchOptions => new()
    {
        Channel = "chrome",
        Headless = true,
        Args = ["--no-proxy-server"],
    };

    /// <summary>
    /// The shared browser, launched on first use. If Chrome has gone away (it crashed), the next
    /// caller launches a new one so one crash does not fail every later test.
    /// </summary>
    public static async Task<IBrowser> SharedAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_browser is { IsConnected: true })
                return _browser;

            // Start over with a fresh driver too: if the driver died, its connection is dead.
            if (_browser is not null)
                await _browser.DisposeAsync();
            _playwright?.Dispose();
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(LaunchOptions);
            return _browser;
        }
        finally
        {
            Gate.Release();
        }
    }
}
