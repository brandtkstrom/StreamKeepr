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
    public void The_url_drives_the_query_chips_and_sort_label()
    {
        TestLibraryView view = OneStreamer();
        using BunitContext context = CreateContext(view);
        context.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/r/r1?state=saved&sort=oldest&q=%20Chef%20");

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Assert.Equal(new LibraryQuery("saved", "Chef", "oldest"), view.LastQuery);
        Assert.Equal("true", Chip(cut, "saved").GetAttribute("aria-pressed"));
        Assert.Equal("false", Chip(cut, "all").GetAttribute("aria-pressed"));
        Assert.Contains("Oldest first", cut.Find(".sk-sort").TextContent);
        Assert.Contains("arrow_upward", cut.Find(".sk-sort").TextContent);
    }

    [Theory]
    [InlineData("newest", "Newest first")]
    [InlineData("az", "A–Z by streamer")]
    [InlineData("bogus", "Newest first")]
    public void The_header_label_follows_the_sort(string sort, string label)
    {
        using BunitContext context = CreateContext(OneStreamer());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/?sort={sort}");

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Assert.Contains(label, cut.Find(".sk-sort").TextContent);
    }

    [Fact]
    public void Chips_replace_the_history_entry_and_keep_other_keys()
    {
        using BunitContext context = CreateContext(OneStreamer());
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
    public void Sort_menu_sets_the_sort_key()
    {
        using BunitContext context = CreateContext(OneStreamer());
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();
        cut.FindAll(".sk-menu-item").Single(item => item.TextContent.Contains("A–Z")).Click();

        Assert.EndsWith("/?sort=az", navigation.Uri);
        cut.WaitForAssertion(() => Assert.Contains("A–Z by streamer", cut.Find(".sk-sort").TextContent));
    }

    [Fact]
    public void Matches_are_highlighted_case_insensitively_in_titles_and_names()
    {
        using BunitContext context = CreateContext(OneStreamer());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/?q=CHEF");

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Assert.Equal("Chef", cut.Find(".sk-group-head .name mark.sk-hl").TextContent);
        Assert.Equal("chef", cut.Find("[data-recording-id='r1'] .sk-row-title mark.sk-hl").TextContent);
        Assert.Equal("Night chef special", cut.Find("[data-recording-id='r1'] .sk-row-title").TextContent);
    }

    [Fact]
    public void No_matches_shows_a_distinct_message_from_an_empty_library()
    {
        var view = new TestLibraryView();
        using BunitContext context = CreateContext(view);
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();
        Assert.Equal("No recordings yet", cut.Find("[data-library-empty]").TextContent);

        navigation.NavigateTo("/?q=zzz");
        cut.WaitForAssertion(() =>
            Assert.Equal("No recordings match", cut.Find("[data-library-empty]").TextContent));
    }

    private static AngleSharp.Dom.IElement Chip(IRenderedComponent<LibraryDrawer> cut, string filter) =>
        cut.Find($".sk-filters [data-filter='{filter}']");

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
        context.Services.AddSingleton<IClientStateStore>(new TestClientStateStore());
        context.Services.AddScoped<ViewerClock>();
        context.Services.AddScoped<SnackbarService>();
        context.Services.AddScoped<UiPreferences>();
        context.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        context.Services.AddSingleton<ILibraryView>(view);
        return context;
    }
}
