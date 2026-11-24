using Microsoft.Playwright;

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

    /// <summary>Every channel option in the channels listbox.</summary>
    public const string Channel = Drawer + " [role=option][data-library-channel]";

    /// <summary>The open channel's recordings column.</summary>
    public const string Column = Drawer + " [data-recordings-column]";

    /// <summary>Every recording row in the open column.</summary>
    public const string Row = Drawer + " a[data-recording-id]";

    /// <summary>Recording rows whose session is complete and saved.</summary>
    public const string SavedRow = Row + "[data-state='saved']";

    /// <summary>One channel's option, by login.</summary>
    public static string ChannelFor(string login) => $"{Drawer} [data-library-channel='{login}']";

    /// <summary>
    /// Opens the library when it is closed: Md and up the menu opens it over the page (it starts
    /// as the rail); below Md the bottom bar's Library opens the temporary drawer.
    /// </summary>
    public static async Task OpenLibraryAsync(IPage page)
    {
        // A click sent before the circuit is interactive is silently dropped, and the test then
        // waits 30 s for a drawer that can never open (#296).
        await Journey.WaitForShellAsync(page);
        await page.Locator("[data-shell]").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        if (await page.Locator("[data-shell][data-library-open]").CountAsync() > 0)
            return;

        ILocator library = page.GetByRole(AriaRole.Navigation, new() { Name = "Main" })
            .GetByRole(AriaRole.Button, new() { Name = "Library" });
        await Journey.WaitForReconnectAsync(page);
        if (await library.IsVisibleAsync())
            await library.ClickAsync();
        else
            await page.Locator("[data-drawer-toggle]").ClickAsync();
        await page.Locator("[data-shell][data-library-open]").WaitForAsync(new() { State = WaitForSelectorState.Attached });
    }

    /// <summary>
    /// Opens a channel's recordings column and waits for it to finish sliding in. It opens the
    /// library first, and on a phone leaves a channel it drilled into.
    /// </summary>
    public static async Task OpenChannelAsync(IPage page, string login)
    {
        await Journey.WaitForReconnectAsync(page);
        ILocator open = page.Locator($"{Drawer} [data-recordings-column='{login}']");
        if (await open.CountAsync() == 1 && await open.IsVisibleAsync())
            return;

        await OpenLibraryAsync(page);
        ILocator back = page.Locator($"{Drawer} button[aria-label='All channels']");
        if (await back.IsVisibleAsync())
        {
            await Journey.WaitForReconnectAsync(page);
            await back.ClickAsync();
        }

        await Journey.WaitForReconnectAsync(page);
        await page.Locator(ChannelFor(login)).ClickAsync();
        await open.WaitForAsync();
        await open.EvaluateAsync("c => Promise.all(c.getAnimations().map(a => a.finished))");
    }

    /// <summary>
    /// Rows show only in the open channel's column, so this opens the channels in list order
    /// until the <paramref name="index"/>-th row matching <paramref name="selector"/> (and
    /// <paramref name="hasText"/>, when given) is there, and returns it, its column left open.
    /// </summary>
    public static async Task<ILocator> RowAsync(
        IPage page, string selector = Row, string? hasText = null, int index = 0)
    {
        // Attached: on a phone the drawer starts closed.
        await page.Locator(Channel).First.WaitForAsync(new() { State = WaitForSelectorState.Attached });
        int channels = await page.Locator(Channel).CountAsync();
        int seen = 0;
        for (int i = 0; i < channels; i++)
        {
            ILocator channel = page.Locator(Channel).Nth(i);
            string login = (await channel.GetAttributeAsync("data-library-channel"))!;
            await OpenChannelAsync(page, login);
            ILocator rows = page.Locator(selector, new() { HasTextString = hasText });
            int count = await rows.CountAsync();
            if (seen + count > index)
                return rows.Nth(index - seen);
            seen += count;
        }

        throw new InvalidOperationException($"No library row #{index} matches {selector} {hasText}.");
    }
}
