using Microsoft.Playwright;

namespace StreamKeepr.E2E;

/// <summary>The title block's actions: Stop recording and Delete live in More actions.</summary>
internal static class TitleBlock
{
    /// <summary>The More actions button.</summary>
    public static ILocator More(IPage page) => page.Locator("button[aria-label='More actions']");

    /// <summary>Opens More actions and returns its item carrying <paramref name="hook"/>.</summary>
    public static async Task<ILocator> MenuItemAsync(IPage page, string hook)
    {
        await More(page).ClickAsync();
        ILocator item = page.Locator($"[role='menuitem'][{hook}]");
        await item.WaitForAsync();
        return item;
    }

    /// <summary>
    /// Closes More actions. MudMenu renders its list in the popover provider, outside the page's
    /// landmarks, so axe checks run with it closed.
    /// </summary>
    public static async Task CloseMenuAsync(IPage page)
    {
        ILocator items = page.Locator("[role='menuitem']");
        await items.First.PressAsync("Escape");
        await items.First.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
    }
}
