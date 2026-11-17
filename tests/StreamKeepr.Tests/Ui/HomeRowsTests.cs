using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Components.Pages;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

/// <summary>Home at <c>/</c>: the Recording now and Recent recordings rows (#182).</summary>
public sealed class HomeRowsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Open_sessions_fill_the_first_row_and_complete_sessions_the_second_newest_first()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out TestLibraryView view);
        Seed(view,
            Card("ann-live", "ann", RecordingState.Recording, hoursAgo: 3),
            Card("bob-back", "bob", RecordingState.Reconnecting, hoursAgo: 1),
            Card("ann-saved", "ann", RecordingState.Saved, hoursAgo: 5),
            Card("bob-partial", "bob", RecordingState.Partial, hoursAgo: 4));

        IRenderedComponent<Home> cut = context.Render<Home>();

        Assert.Equal(["bob-back", "ann-live"], CardIds(cut, "Recording now"));
        Assert.Equal(["bob-partial", "ann-saved"], CardIds(cut, "Recent recordings"));
        Assert.All(Cards(cut, "Recording now"), card => Assert.Equal("large", card.GetAttribute("data-card-size")));
        Assert.All(Cards(cut, "Recent recordings"), card => Assert.Equal("default", card.GetAttribute("data-card-size")));
        Assert.Equal("/r/bob-back", Cards(cut, "Recording now")[0].GetAttribute("href"));
        Assert.Equal("2", cut.Find("[data-row-count]").TextContent.Trim());
        Assert.Equal("Recording, Bob B, reconnecting", RecordingCardTests.AccessibleName(Cards(cut, "Recording now")[0]));
    }

    [Fact]
    public async Task Recent_recordings_hold_the_twenty_newest_and_recording_now_is_never_truncated()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out TestLibraryView view);
        Seed(view,
        [
            .. Enumerable.Range(0, 25).Select(i => Card($"saved-{i:00}", "ann", RecordingState.Saved, hoursAgo: 10 + i)),
            .. Enumerable.Range(0, 22).Select(i => Card($"live-{i:00}", "bob", RecordingState.Recording, hoursAgo: i)),
        ]);

        IRenderedComponent<Home> cut = context.Render<Home>();

        Assert.Equal(22, CardIds(cut, "Recording now").Count);
        Assert.Equal(
            Enumerable.Range(0, 20).Select(i => $"saved-{i:00}"),
            CardIds(cut, "Recent recordings"));
    }

    [Fact]
    public async Task With_nothing_recording_the_first_row_keeps_its_heading_and_shows_one_note()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out TestLibraryView view);
        Seed(view, Card("ann-saved", "ann", RecordingState.Saved, hoursAgo: 5));

        IRenderedComponent<Home> cut = context.Render<Home>();

        Assert.NotNull(Heading(cut, "Recording now"));
        Assert.Empty(Cards(cut, "Recording now"));
        Assert.Equal("0", cut.Find("[data-row-count]").TextContent.Trim());
        Assert.StartsWith("Nothing is recording right now.", cut.Find("[data-nothing-recording]").TextContent.Trim());
        Assert.Equal(["ann-saved"], CardIds(cut, "Recent recordings"));
    }

    [Fact]
    public async Task While_the_library_loads_each_row_shows_three_skeleton_cards()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out TestLibraryView view);
        view.IsLoaded = false;
        Seed(view, Card("ann-saved", "ann", RecordingState.Saved, hoursAgo: 5));

        IRenderedComponent<Home> cut = context.Render<Home>();

        Assert.NotNull(Heading(cut, "Recording now"));
        Assert.NotNull(Heading(cut, "Recent recordings"));
        Assert.Equal(6, cut.FindAll("[data-card-skeleton]").Count);
        Assert.Empty(cut.FindAll("a[data-recording-id]"));
        Assert.Empty(cut.FindAll("[data-nothing-recording]"));
    }

    [Fact]
    public async Task A_session_moves_to_the_second_row_when_it_completes()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out TestLibraryView view);
        Seed(view, Card("ann-live", "ann", RecordingState.Recording, hoursAgo: 1));
        IRenderedComponent<Home> cut = context.Render<Home>();
        Assert.Equal(["ann-live"], CardIds(cut, "Recording now"));

        Seed(view, Card("ann-live", "ann", RecordingState.Saved, hoursAgo: 1));
        view.RaiseChanged();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(CardIds(cut, "Recording now"));
            Assert.Equal(["ann-live"], CardIds(cut, "Recent recordings"));
        });
    }

    [Fact]
    public async Task Show_all_opens_the_library_on_the_saved_filter()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out TestLibraryView view);
        Seed(view, Card("ann-saved", "ann", RecordingState.Saved, hoursAgo: 5));
        IRenderedComponent<Home> cut = context.Render<Home>();

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Show all").Click();

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        Assert.Equal("?state=saved", navigation.ToBaseRelativePath(navigation.Uri));
    }

    [Fact]
    public async Task An_empty_library_shows_no_rows()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out _);

        IRenderedComponent<Home> cut = context.Render<Home>();

        Assert.Empty(cut.FindAll("[data-home-rows]"));
    }

    [Fact]
    public async Task A_watch_page_shows_no_rows()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out TestLibraryView view);
        Seed(view, Card("ann-saved", "ann", RecordingState.Saved, hoursAgo: 5));

        IRenderedComponent<Home> cut = context.Render<Home>(p => p.Add(c => c.RecordingId, "r1"));

        Assert.Empty(cut.FindAll("[data-home-rows]"));
    }

    private static IElement? Heading(IRenderedComponent<Home> cut, string name) =>
        cut.FindAll("h2").SingleOrDefault(heading => heading.TextContent.Trim() == name);

    private static IReadOnlyList<IElement> Cards(IRenderedComponent<Home> cut, string row)
    {
        string id = Heading(cut, row)!.Id!;
        IElement? list = cut.FindAll($"ul[aria-labelledby='{id}']").SingleOrDefault();
        return list is null ? [] : [.. list.QuerySelectorAll("li > a[data-recording-id]")];
    }

    private static IReadOnlyList<string> CardIds(IRenderedComponent<Home> cut, string row) =>
        [.. Cards(cut, row).Select(card => card.GetAttribute("data-recording-id")!)];

    private static void Seed(TestLibraryView view, params RecordingView[] recordings)
    {
        view.StreamerItems =
        [
            new StreamerView("ann", "Ann A", null, false, false, true, 0, null),
            new StreamerView("bob", "Bob B", null, false, false, true, 0, null),
        ];
        view.RecordingsByLogin.Clear();
        foreach (IGrouping<string, RecordingView> group in recordings.GroupBy(recording => recording.StreamerLogin))
            view.RecordingsByLogin[group.Key] = [.. group];
    }

    private static RecordingView Card(string id, string login, RecordingState state, int hoursAgo) =>
        RecordingCardTests.Recording(id, state, startedAt: Now.AddHours(-hoursAgo), login: login);
}
