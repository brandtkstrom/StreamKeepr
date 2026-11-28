using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace StreamKeepr.Tests.Ui;

/// <summary>Drives the title block's public More actions menu.</summary>
internal static class MoreActions
{
    /// <summary>
    /// Opening the menu takes a dozen renders through the popover provider. On a loaded CI
    /// runner bUnit's 1 s default has expired before the first check ran (run 46117858176:
    /// check count 0, 13 renders), so this wait gets the margin other slow waits here use.
    /// A passing check still returns at once.
    /// </summary>
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Opens More actions and returns its item carrying <paramref name="hook"/> (a data
    /// attribute such as <c>data-delete</c>). MudMenu renders its list in the popover provider;
    /// pass an existing one when a test opens the menu more than once.
    /// </summary>
    public static IElement Item<T>(
        BunitContext context,
        IRenderedComponent<T> cut,
        string hook,
        IRenderedComponent<MudBlazor.MudPopoverProvider>? popovers = null)
        where T : IComponent
    {
        popovers ??= context.Render<MudBlazor.MudPopoverProvider>();
        IElement? item = popovers.FindAll($"[{hook}]").FirstOrDefault();
        if (item is null)
        {
            cut.Find("button[aria-label='More actions']").Click();
            popovers.WaitForAssertion(() => item = popovers.Find($"[{hook}]"), OpenTimeout);
        }

        return item!;
    }
}
