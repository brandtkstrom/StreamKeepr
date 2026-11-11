using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Components.Features.Library;
using StreamKeepr.Components.Features.Player;
using StreamKeepr.Components.Layout;
using StreamKeepr.Components.Pages;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class LibraryDrawerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Renders_streamer_groups_and_rows_as_real_links()
    {
        var view = new TestLibraryView
        {
            StreamerItems =
            [
                Streamer("live", "Live Star", recording: true, count: 2, last: Now),
                Streamer("idle", "Idle Star", recording: false, count: 1, last: Now.AddDays(-2)),
            ],
        };

        view.RecordingsByLogin["live"] =
        [
            Recording("r1", "live", RecordingState.Recording, Now.AddMinutes(-5)),
            Recording("r2", "live", RecordingState.Saved, Now.AddDays(-2)),
        ];
        view.RecordingsByLogin["idle"] =
        [
            Recording("r3", "idle", RecordingState.Saved, Now.AddDays(-3)),
        ];

        using BunitContext context = CreateContext(view);
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        var groups = cut.FindAll(".sk-group");
        Assert.Equal(2, groups.Count);
        Assert.Contains("Live Star", groups[0].TextContent);
        Assert.Contains("Recording now", groups[0].TextContent);
        Assert.Contains("Idle Star", groups[1].TextContent);

        Assert.Equal(3, cut.FindAll("a.sk-row").Count);
        Assert.Equal("/r/r1", cut.Find("a[data-recording-id='r1']").GetAttribute("href"));
    }

    [Fact]
    public void Renders_status_chips_except_saved()
    {
        var view = new TestLibraryView
        {
            StreamerItems = [Streamer("alpha", "Alpha", recording: true, count: 4, last: Now)],
        };
        view.RecordingsByLogin["alpha"] =
        [
            Recording("rec", "alpha", RecordingState.Recording, Now.AddMinutes(-1)),
            Recording("reconnect", "alpha", RecordingState.Reconnecting, Now.AddHours(-1)),
            Recording("partial", "alpha", RecordingState.Partial, Now.AddHours(-2), parts: 2),
            Recording("saved", "alpha", RecordingState.Saved, Now.AddHours(-3)),
        ];

        using BunitContext context = CreateContext(view);
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Assert.Contains("Rec", cut.Find("[data-recording-id='rec'] .sk-status.rec").TextContent);
        Assert.Contains(
            "Reconnecting",
            cut.Find("[data-recording-id='reconnect'] .sk-status.partial").TextContent);
        Assert.Contains(
            "Partial",
            cut.Find("[data-recording-id='partial'] .sk-status.partial").TextContent);
        Assert.Empty(cut.Find("[data-recording-id='saved']").QuerySelectorAll(".sk-status"));
    }

    [Fact]
    public void Marks_the_selected_row_current_and_follows_navigation()
    {
        var view = new TestLibraryView
        {
            StreamerItems = [Streamer("alpha", "Alpha", recording: false, count: 2, last: Now)],
        };
        view.RecordingsByLogin["alpha"] =
        [
            Recording("r1", "alpha", RecordingState.Saved, Now.AddHours(-1)),
            Recording("r2", "alpha", RecordingState.Saved, Now.AddHours(-2)),
        ];

        using BunitContext context = CreateContext(view);
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/r/r2");

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Assert.Equal("true", cut.Find("[data-recording-id='r2']").GetAttribute("aria-current"));
        Assert.Null(cut.Find("[data-recording-id='r1']").GetAttribute("aria-current"));

        navigation.NavigateTo("/r/r1");

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("true", cut.Find("[data-recording-id='r1']").GetAttribute("aria-current"));
            Assert.Null(cut.Find("[data-recording-id='r2']").GetAttribute("aria-current"));
        });
    }

    [Fact]
    public void Groups_rows_under_viewer_zone_day_headers()
    {
        var view = new TestLibraryView
        {
            StreamerItems = [Streamer("alpha", "Alpha", recording: false, count: 3, last: Now)],
        };
        RecordingView today = Recording("today", "alpha", RecordingState.Saved, Now.AddHours(-2));
        RecordingView yesterday = Recording(
            "yesterday", "alpha", RecordingState.Saved, Now.AddDays(-1));
        RecordingView older = Recording(
            "older", "alpha", RecordingState.Saved, Now.AddDays(-8));
        view.RecordingsByLogin["alpha"] = [today, yesterday, older];

        using BunitContext context = CreateContext(view);
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        IReadOnlyList<string> days = cut.FindAll(".sk-day").Select(day => day.TextContent).ToArray();
        Assert.Equal(
            [
                "Today",
                "Yesterday",
                Format.DayHeader(older.StartedAt, Now, TimeZoneInfo.Utc),
            ],
            days);
        Assert.Equal(1, days.Count(day => day == "Yesterday"));
    }

    [Fact]
    public void Shows_skeletons_until_loaded_and_an_empty_message_afterward()
    {
        var view = new TestLibraryView { IsLoaded = false };

        using BunitContext context = CreateContext(view);
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Assert.Equal(6, cut.FindAll("[data-library-skeleton]").Count);
        Assert.Empty(cut.FindAll("[data-library-empty]"));

        view.IsLoaded = true;
        view.RaiseChanged();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-library-skeleton]"));
            Assert.Single(cut.FindAll("[data-library-empty]"));
            Assert.Contains("No recordings yet", cut.Find("[data-library-empty]").TextContent);
        });
    }

    [Fact]
    public void Collapsing_a_group_sets_aria_expanded()
    {
        var view = new TestLibraryView
        {
            StreamerItems = [Streamer("alpha", "Alpha", recording: false, count: 1, last: Now)],
        };
        view.RecordingsByLogin["alpha"] =
        [
            Recording("r1", "alpha", RecordingState.Saved, Now.AddHours(-1)),
        ];

        using BunitContext context = CreateContext(view);
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        cut.Find(".sk-group-head").Click();

        cut.WaitForAssertion(() =>
        {
            var header = cut.Find(".sk-group-head");
            Assert.Equal("false", header.GetAttribute("aria-expanded"));
            Assert.Equal(
                header.GetAttribute("aria-controls"),
                cut.Find(".sk-group-body").GetAttribute("id"));
        });
    }

    [Fact]
    public async Task Reused_view_models_skip_rendering_and_changed_rows_render()
    {
        var view = new TestLibraryView
        {
            StreamerItems = [Streamer("alpha", "Alpha", recording: false, count: 1, last: Now)],
        };
        RecordingView original = Recording(
            "r1", "alpha", RecordingState.Saved, Now.AddHours(-1));
        view.RecordingsByLogin["alpha"] = [original];

        using BunitContext context = CreateContext(view);
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        IRenderedComponent<StreamerGroup> group = cut.FindComponent<StreamerGroup>();
        IRenderedComponent<RecordingRow> row = cut.FindComponent<RecordingRow>();
        int groupRenders = 0;
        int rowRenders = 0;
        group.OnAfterRender += (_, _) => groupRenders++;
        row.OnAfterRender += (_, _) => rowRenders++;

        // Deliver the initial render's lifecycle before measuring the reused update.
        await cut.InvokeAsync(view.RaiseChanged);
        cut.WaitForAssertion(() => Assert.True(groupRenders > 0));
        groupRenders = 0;
        rowRenders = 0;

        await cut.InvokeAsync(view.RaiseChanged);

        Assert.Equal(0, groupRenders);
        Assert.Equal(0, rowRenders);

        view.RecordingsByLogin["alpha"] =
        [
            Recording("r1", "alpha", RecordingState.Partial, Now.AddHours(-1), parts: 2),
        ];

        await cut.InvokeAsync(view.RaiseChanged);

        cut.WaitForAssertion(() =>
        {
            Assert.True(rowRenders > 0);
            Assert.NotNull(cut.Find("[data-recording-id='r1'] .sk-status.partial"));
        });
    }

    [Fact]
    public void Day_headers_use_the_viewer_time_zone()
    {
        var view = new TestLibraryView
        {
            StreamerItems = [Streamer("alpha", "Alpha", recording: false, count: 1, last: Now)],
        };
        view.RecordingsByLogin["alpha"] =
        [
            Recording("r1", "alpha", RecordingState.Saved, Now.AddHours(-14)),
        ];

        using BunitContext context = CreateContext(view, timeZoneId: "Asia/Tokyo");
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Assert.Equal("Today", cut.Find(".sk-day").TextContent);
    }

    [Fact]
    public void Main_layout_lights_the_recording_dot_and_warns_below_ten_percent()
    {
        var view = new TestLibraryView
        {
            Storage = new StorageView(5L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024),
            StreamerItems = [Streamer("alpha", "Alpha", recording: true, count: 1, last: Now)],
        };
        view.RecordingsByLogin["alpha"] =
        [
            Recording("r1", "alpha", RecordingState.Recording, Now.AddMinutes(-1)),
        ];

        using BunitContext context = CreateContext(view);
        IRenderedComponent<MainLayout> cut = context.Render<MainLayout>(parameters =>
            parameters.Add(component => component.Body, fragment =>
                fragment.AddMarkupContent(0, "<div data-test='body'>Body</div>")));

        Assert.NotNull(cut.Find(".sk-wordmark .rec-dot"));
        Assert.True(cut.Find(".sk-storage").ClassList.Contains("warning"));
        Assert.Contains("5.0 GB free of 100.0 GB", cut.Find(".sk-storage").GetAttribute("title"));
    }

    [Fact]
    public void Unknown_recording_renders_a_message_and_link_back()
    {
        var view = new TestLibraryView { IsLoaded = true };

        using BunitContext context = CreateContext(view);
        IRenderedComponent<Home> cut = context.Render<Home>(parameters =>
            parameters.Add(component => component.RecordingId, "missing"));

        Assert.NotNull(cut.Find("[data-unknown-recording]"));
        Assert.Contains("Recording not found", cut.Find("[data-unknown-recording]").TextContent);
        Assert.Equal("/", cut.Find("[data-unknown-recording] a").GetAttribute("href"));
    }

    [Fact]
    public void Known_recording_renders_the_selected_title()
    {
        var view = new TestLibraryView { IsLoaded = true };
        view.FindById["r1"] = Recording("r1", "alpha", RecordingState.Saved, Now.AddHours(-1));

        using BunitContext context = CreateContext(view);
        IRenderedComponent<Home> cut = context.Render<Home>(parameters =>
            parameters.Add(component => component.RecordingId, "r1"));

        Assert.Equal(
            "r1",
            cut.Find("[data-selected-recording]").GetAttribute("data-selected-recording"));
        Assert.Contains("Title r1", cut.Find("[data-selected-recording]").TextContent);
    }

    private static IRenderedComponent<LibraryDrawer> RenderDrawer(BunitContext context) =>
        context.Render<LibraryDrawer>();

    private static BunitContext CreateContext(
        TestLibraryView view,
        string? timeZoneId = null)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();

        var store = new TestClientStateStore();
        if (timeZoneId is not null)
            store.Cookies[ViewerClock.CookieName] = timeZoneId;

        context.Services.AddSingleton<IClientStateStore>(store);
        context.Services.AddScoped<ViewerClock>();
        context.Services.AddScoped<SnackbarService>();
        context.Services.AddScoped<PlayerBridge>();
        context.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        context.Services.AddSingleton<ILibraryView>(view);
        context.Services.AddScoped<UiPreferences>();

        return context;
    }

    private static StreamerView Streamer(
        string login,
        string displayName,
        bool recording,
        int count,
        DateTimeOffset? last,
        bool paused = false) =>
        new(login, displayName, null, recording, paused, IsWatched: true, count, last);

    private static RecordingView Recording(
        string id,
        string login,
        RecordingState state,
        DateTimeOffset startedAt,
        int parts = 1)
    {
        bool incomplete = state is RecordingState.Recording or RecordingState.Reconnecting;

        return new RecordingView(
            id,
            login,
            $"Title {id}",
            "Game",
            startedAt,
            incomplete ? null : startedAt.AddHours(1),
            incomplete ? 420 : 3600,
            state,
            SegmentCount: 8,
            PartCount: parts,
            SizeBytes: incomplete ? null : 1024,
            ThumbnailUrl: null,
            PlaylistUrl: $"/media/{login}/{id}/index.m3u8",
            ResumeSec: null,
            Chapters: []);
    }
}

internal sealed class TestLibraryView : ILibraryView
{
    public bool IsLoaded { get; set; } = true;

    public IReadOnlyList<StreamerView> StreamerItems { get; set; } = [];

    public Dictionary<string, IReadOnlyList<RecordingView>> RecordingsByLogin { get; } =
        new(StringComparer.Ordinal);

    public Dictionary<string, RecordingView> FindById { get; } =
        new(StringComparer.Ordinal);

    public StorageView Storage { get; set; } = new(50L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024);

    public event Action? Changed;

    public LibraryQuery? LastQuery { get; private set; }

    public IReadOnlyList<StreamerView> Streamers(LibraryQuery query)
    {
        LastQuery = query;
        return StreamerItems;
    }

    public IReadOnlyList<RecordingView> Recordings(string streamerLogin, LibraryQuery query) =>
        RecordingsByLogin.GetValueOrDefault(streamerLogin) ?? [];

    public RecordingView? Find(string id) => FindById.GetValueOrDefault(id);

    public void RaiseChanged() => Changed?.Invoke();

    public void Dispose()
    {
    }
}
