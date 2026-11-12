using Microsoft.Playwright;

namespace StreamKeepr.E2E;

/// <summary>
/// How every journey launches Chrome. It is branded Chrome because Playwright's bundled
/// Chromium lacks H.264 (E-21). The suite only talks to the Mock app on loopback, so the
/// browser skips the system proxy settings. Without that, a machine set to auto-detect a proxy
/// (WPAD/PAC) waits about 6 s on every fresh browser's first request before it connects.
/// </summary>
internal static class E2EBrowser
{
    public static BrowserTypeLaunchOptions LaunchOptions => new()
    {
        Channel = "chrome",
        Headless = true,
        Args = ["--no-proxy-server"],
    };
}
