using Microsoft.Playwright;

namespace StreamKeepr.E2E;

/// <summary>
/// Role-based locators for transient feedback (design system <b>Feedback</b> card). A MudBlazor
/// snackbar is an <c>alert</c> with a polite live region; other alerts on the page (an error
/// banner in a dialog) carry different text, so the text picks the snackbar out.
/// </summary>
internal static class Feedback
{
    /// <summary>The snackbar announcing <paramref name="text"/>.</summary>
    public static ILocator Snackbar(IPage page, string text) =>
        page.GetByRole(AriaRole.Alert).Filter(new() { HasTextString = text });
}
