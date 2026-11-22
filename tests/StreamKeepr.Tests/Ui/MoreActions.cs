using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace StreamKeepr.Tests.Ui;

/// <summary>Drives the title block's More actions menu, where Stop recording and Delete live.</summary>
internal static class MoreActions
{
    /// <summary>
    /// Opens More actions and returns its item carrying <paramref name="hook"/> (a data
    /// attribute such as <c>data-delete</c>). MudMenu renders its list in the popover provider.
    /// </summary>
    public static IElement Item<T>(BunitContext context, IRenderedComponent<T> cut, string hook)
        where T : IComponent
    {
        IRenderedComponent<MudBlazor.MudPopoverProvider> popovers = context.Render<MudBlazor.MudPopoverProvider>();
        cut.Find("button[aria-label='More actions']").Click();
        IElement? item = null;
        popovers.WaitForAssertion(() => item = popovers.Find($"[{hook}]"));
        return item!;
    }
}
