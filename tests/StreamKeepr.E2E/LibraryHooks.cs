namespace StreamKeepr.E2E;

/// <summary>
/// Shared selectors for the library drawer, built from its landmark and data hooks so they
/// survive styling changes (docs/testing-contract.md, Selectors). Selectors inside page
/// scripts repeat these strings because they run in the browser.
/// </summary>
internal static class LibraryHooks
{
    /// <summary>The library navigation landmark.</summary>
    public const string Drawer = "nav[aria-label=Library]";

    /// <summary>Every recording row in the library.</summary>
    public const string Row = Drawer + " a[data-recording-id]";

    /// <summary>Recording rows whose session is complete and saved.</summary>
    public const string SavedRow = Row + "[data-state='saved']";

    /// <summary>One streamer's group: its tree item head and its rows.</summary>
    public const string Group = Drawer + " [data-streamer-group]";
}
