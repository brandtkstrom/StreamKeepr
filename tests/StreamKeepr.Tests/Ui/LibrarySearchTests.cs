using MudBlazor.Services;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Components.Features.Library;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class LibrarySearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_url_drives_the_query_chips_and_sort_label()
    {
        TestLibraryView view = OneStreamer();
        await using BunitContext context = CreateContext(view);
        context.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/r/r1?state=saved&sort=oldest&q=%20Chef%20");

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Assert.Equal(new LibraryQuery("saved", "Chef", "oldest"), view.LastQuery);
        Assert.Equal("true", Chip(cut, "saved").GetAttribute("aria-pressed"));
        Assert.Equal("false", Chip(cut, "all").GetAttribute("aria-pressed"));
        Assert.Contains("Oldest first", cut.Find("[data-sort]").TextContent);
        // The arrow is a MudIcon SVG now, named by a hook (#176).
        Assert.Equal("arrow_upward", cut.Find("[data-sort]").GetAttribute("data-sort-icon"));
    }

    [Theory]
    [InlineData("newest", "Newest first")]
    [InlineData("az", "A–Z by streamer")]
    [InlineData("bogus", "Newest first")]
    public async Task The_header_label_follows_the_sort(string sort, string label)
    {
        await using BunitContext context = CreateContext(OneStreamer());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/?sort={sort}");

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Assert.Contains(label, cut.Find("[data-sort]").TextContent);
    }

    [Fact]
    public async Task Chips_replace_the_history_entry_and_keep_other_keys()
    {
        await using BunitContext context = CreateContext(OneStreamer());
        var navigation = (BunitNavigationManager)context.Services
            .GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/r/r1?q=chef");

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();
        Chip(cut, "recording").Click();

        Assert.EndsWith("/r/r1?q=chef&state=recording", navigation.Uri);
        Assert.True(navigation.History.First().Options.ReplaceHistoryEntry);
        cut.WaitForAssertion(() =>
            Assert.Equal("true", Chip(cut, "recording").GetAttribute("aria-pressed")));

        Chip(cut, "all").Click();
        Assert.EndsWith("/r/r1?q=chef", navigation.Uri);
    }

    [Fact]
    public async Task Sort_menu_sets_the_sort_key()
    {
        await using BunitContext context = CreateContext(OneStreamer());
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();

        // MudMenu renders its items in the popover provider, and only while it is open (#176).
        BunitJSModuleInterop focus = context.JSInterop.SetupModule(
            "./Components/Features/Library/LibraryDrawer.razor.js");
        IRenderedComponent<MudBlazor.MudPopoverProvider> popovers = context.Render<MudBlazor.MudPopoverProvider>();
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();
        cut.Find("button[aria-label='Library options']").Click();
        popovers.WaitForAssertion(() => Assert.NotEmpty(popovers.FindAll("[role='menuitem']")));

        // Opening puts focus on the current choice, so one Escape closes the menu.
        cut.WaitForAssertion(() => Assert.Contains(
            focus.Invocations,
            call => call.Identifier == "focusWhenPresent" && Equals(call.Arguments[0], "[data-sort-option='newest']")));
        Assert.Equal("true", popovers.Find("[data-sort-option='newest']").GetAttribute("data-selected"));
        // Assistive tech hears which order is current, not only sees the check.
        Assert.Equal("true", popovers.Find("[data-sort-option='newest']").GetAttribute("aria-current"));
        Assert.Null(popovers.Find("[data-sort-option='az']").GetAttribute("aria-current"));
        popovers.FindAll("[role='menuitem']").Single(item => item.TextContent.Contains("A–Z")).Click();

        Assert.EndsWith("/?sort=az", navigation.Uri);
        cut.WaitForAssertion(() => Assert.Contains("A–Z by streamer", cut.Find("[data-sort]").TextContent));
    }

    [Fact]
    public async Task Matches_are_highlighted_case_insensitively_in_titles_and_names()
    {
        await using BunitContext context = CreateContext(OneStreamer());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/?q=CHEF");

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Assert.Equal("Chef", cut.Find("[role='treeitem'][aria-level='1'] mark").TextContent);
        Assert.Equal("chef", cut.Find("[data-recording-id='r1'] [data-row-title] mark").TextContent);
        Assert.Equal("Night chef special", cut.Find("[data-recording-id='r1'] [data-row-title]").TextContent);
    }

    [Fact]
    public async Task Row_links_keep_the_library_view_so_selection_does_not_reset_it()
    {
        await using BunitContext context = CreateContext(OneStreamer());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/?q=chef&sort=oldest");

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Assert.Equal("/r/r1?q=chef&sort=oldest", cut.Find("a[data-recording-id='r1']").GetAttribute("href"));
    }

    [Fact]
    public async Task No_matches_shows_a_distinct_message_from_an_empty_library()
    {
        var view = new TestLibraryView();
        await using BunitContext context = CreateContext(view);
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();
        Assert.Equal("No recordings yet", cut.Find("[data-library-empty]").TextContent);

        navigation.NavigateTo("/?q=zzz");
        cut.WaitForAssertion(() =>
            Assert.Equal("No recordings match", cut.Find("[data-library-empty]").TextContent));
    }

    private static AngleSharp.Dom.IElement Chip(IRenderedComponent<LibraryDrawer> cut, string filter) =>
        cut.Find($"[role='group'][aria-label='Filter recordings'] [data-filter='{filter}']");

    private static TestLibraryView OneStreamer()
    {
        var view = new TestLibraryView
        {
            StreamerItems =
            [
                new StreamerView("ann", "Chef Ann", null, false, false, true, 1, Now),
            ],
        };
        view.RecordingsByLogin["ann"] =
        [
            new RecordingView(
                "r1", "ann", "Night chef special", "Food", Now.AddHours(-2), Now.AddHours(-1),
                3600, RecordingState.Saved, 8, 1, 1024, null, "/media/ann/r1/index.m3u8",
                null, []),
        ];
        return view;
    }

    private static BunitContext CreateContext(TestLibraryView view)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();
        context.Services.AddMudServices();
        context.Services.AddSingleton<IClientStateStore>(new TestClientStateStore());
        context.Services.AddScoped<ViewerClock>();
        context.Services.AddScoped<SnackbarService>();
        context.Services.AddScoped<UiPreferences>();
        context.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        context.Services.AddSingleton<ILibraryView>(view);
        return context;
    }
}
