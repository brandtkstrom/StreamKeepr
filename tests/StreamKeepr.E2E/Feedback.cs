using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace StreamKeepr.E2E;

/// <summary>
/// Role-based locators for feedback and confirmations (design system <b>Feedback</b> and
/// <b>Dialog</b> cards). A MudBlazor snackbar is an <c>alert</c> with a polite live region;
/// other alerts on the page (an error banner in a dialog) carry different text, so the text
/// picks the snackbar out. A confirmation is a <c>dialog</c> named by its title.
/// </summary>
internal static class Feedback
{
    /// <summary>The snackbar announcing <paramref name="text"/>.</summary>
    public static ILocator Snackbar(IPage page, string text) =>
        page.GetByRole(AriaRole.Alert).Filter(new() { HasTextString = text });

    /// <summary>The open dialog titled exactly <paramref name="name"/>.</summary>
    public static ILocator Dialog(IPage page, string name) =>
        page.GetByRole(AriaRole.Dialog, new() { Name = name, Exact = true });

    /// <summary>The open dialog whose title matches <paramref name="name"/>.</summary>
    public static ILocator Dialog(IPage page, Regex name) =>
        page.GetByRole(AriaRole.Dialog, new() { NameRegex = name });
}
