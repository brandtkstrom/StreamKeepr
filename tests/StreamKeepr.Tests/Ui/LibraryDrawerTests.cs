using Microsoft.Extensions.Options;
using MudBlazor.Services;
using StreamKeepr.Configuration;
using Bunit.TestDoubles;
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
    public async Task Renders_streamer_groups_and_rows_as_real_links()
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

        await using BunitContext context = CreateContext(view);
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        var groups = cut.FindAll("[role='treeitem'][aria-level='1']");
        Assert.Equal(2, groups.Count);
        Assert.Contains("Live Star", groups[0].TextContent);
        Assert.Contains("Recording now", groups[0].TextContent);
        Assert.Contains("Idle Star", groups[1].TextContent);

        Assert.Equal(3, cut.FindAll("a[data-recording-id]").Count);
        Assert.Equal("/r/r1", cut.Find("a[data-recording-id='r1']").GetAttribute("href"));
    }

    [Fact]
    public async Task Each_group_avatar_takes_its_logins_channel_colour()
    {
        // The library lists any login with recordings, watched or not, so a removed channel's
        // recordings keep the colour: it comes from the login alone.
        var view = new TestLibraryView
        {
            StreamerItems =
            [
                Streamer("live", "Live Star", recording: true, count: 1, last: Now),
                Streamer("removed_one", "Gone Now", recording: false, count: 1, last: Now.AddDays(-2)),
            ],
        };
        view.RecordingsByLogin["live"] = [Recording("r1", "live", RecordingState.Recording, Now)];
        view.RecordingsByLogin["removed_one"] = [Recording("r2", "removed_one", RecordingState.Saved, Now.AddDays(-2))];

        await using BunitContext context = CreateContext(view);
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        foreach (string login in new[] { "live", "removed_one" })
        {
            var avatar = cut.Find($"[data-tree-key='g:{login}'] [role='img']");
            Assert.Equal(ChannelColor.For(login).ToString(), avatar.GetAttribute("data-channel-color"));
        }

        Assert.NotNull(cut.Find("[data-tree-key='g:live'] [data-live-ring]"));
        Assert.Empty(cut.FindAll("[data-tree-key='g:removed_one'] [data-live-ring]"));
    }

    [Fact]
    public async Task The_footer_shows_disk_usage_and_warns_below_ten_percent()
    {
        const long GiB = 1024L * 1024 * 1024;
        var view = new TestLibraryView { Storage = new StorageView(20 * GiB, 100 * GiB) };
        await using BunitContext context = CreateContext(view);
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        var meter = cut.Find("[data-storage]");
        Assert.Equal("ok", meter.GetAttribute("data-storage"));
        Assert.Contains("20.0 GB free", meter.TextContent);
        Assert.Contains("of 100.0 GB", meter.TextContent);
        Assert.Equal("80", meter.QuerySelector("[data-storage-used]")!.GetAttribute("data-storage-used"));

        view.Storage = new StorageView(5 * GiB, 100 * GiB);
        view.RaiseChanged();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("low", cut.Find("[data-storage]").GetAttribute("data-storage"));
            Assert.Equal("Recording disk: 5.0 GB free of 100.0 GB", cut.Find("[data-storage]").GetAttribute("title"));
        });
    }

    [Fact]
    public async Task An_unknown_disk_size_shows_no_meter()
    {
        await using BunitContext context = CreateContext(new TestLibraryView { Storage = new StorageView(0, 0) });
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Assert.Empty(cut.FindAll("[data-storage]"));
        Assert.Empty(cut.FindAll("[data-storage-rail]"));
    }

    [Fact]
    public async Task In_the_rail_the_footer_is_a_storage_button_that_opens_the_drawer()
    {
        const long GiB = 1024L * 1024 * 1024;
        var view = new TestLibraryView { Storage = new StorageView(5 * GiB, 100 * GiB) };
        await using BunitContext context = CreateContext(view);
        UiPreferences preferences = context.Services.GetRequiredService<UiPreferences>();
        preferences.SetDrawer(DrawerMode.Rail);

        var cut = context.Render<CascadingValue<bool>>(parameters => parameters
            .Add(component => component.Name, "LibraryRail")
            .Add(component => component.Value, true)
            .AddChildContent<LibraryDrawer>());

        Assert.Empty(cut.FindAll("[data-storage]"));
        var button = cut.Find("button[data-storage-rail]");
        Assert.Equal("low", button.GetAttribute("data-storage-rail"));
        Assert.Equal("Recording disk: 5.0 GB free of 100.0 GB", button.GetAttribute("aria-label"));
        Assert.Equal("/", cut.Find("a[aria-label='Home']").GetAttribute("href"));

        await button.ClickAsync(new());

        Assert.Equal(DrawerMode.Expanded, preferences.Drawer);
    }

    [Fact]
    public async Task Rows_show_the_thumbnail_image_when_one_exists_and_the_placeholder_icon_otherwise()
    {
        var view = new TestLibraryView
        {
            StreamerItems = [Streamer("alpha", "Alpha", recording: false, count: 2, last: Now)],
        };
        view.RecordingsByLogin["alpha"] =
        [
            Recording("r1", "alpha", RecordingState.Saved, Now) with { ThumbnailUrl = "/media/alpha/r1/thumb.jpg" },
            Recording("r2", "alpha", RecordingState.Saved, Now.AddHours(-2)),
        ];

        await using BunitContext context = CreateContext(view);
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        var image = cut.Find("a[data-recording-id='r1'] img");
        Assert.Equal("/media/alpha/r1/thumb.jpg", image.GetAttribute("src"));
        Assert.Equal("", image.GetAttribute("alt"));
        Assert.Equal("lazy", image.GetAttribute("loading"));
        Assert.Empty(cut.FindAll("a[data-recording-id='r1'] [data-icon='movie']"));

        Assert.Empty(cut.FindAll("a[data-recording-id='r2'] img"));
        Assert.Single(cut.FindAll("a[data-recording-id='r2'] [data-icon='movie']"));
    }

    [Fact]
    public async Task Renders_status_chips_except_saved()
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

        await using BunitContext context = CreateContext(view);
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Assert.Contains("Rec", cut.Find("[data-recording-id='rec'] [data-status='rec']").TextContent);
        Assert.Contains(
            "Reconnecting",
            cut.Find("[data-recording-id='reconnect'] [data-status='reconnecting']").TextContent);
        Assert.Contains(
            "Partial",
            cut.Find("[data-recording-id='partial'] [data-status='partial']").TextContent);
        Assert.Empty(cut.Find("[data-recording-id='saved']").QuerySelectorAll("[data-status]"));
    }

    [Fact]
    public async Task Marks_the_selected_row_current_and_follows_navigation()
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

        await using BunitContext context = CreateContext(view);
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
    public async Task Groups_rows_under_viewer_zone_day_headers()
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

        await using BunitContext context = CreateContext(view);
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        IReadOnlyList<string> days = cut.FindAll("[data-day-header]").Select(day => day.TextContent).ToArray();
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
    public async Task Shows_skeletons_until_loaded_and_an_empty_message_afterward()
    {
        var view = new TestLibraryView { IsLoaded = false };

        await using BunitContext context = CreateContext(view);
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
    public async Task Collapsing_a_group_sets_aria_expanded()
    {
        var view = new TestLibraryView
        {
            StreamerItems = [Streamer("alpha", "Alpha", recording: false, count: 1, last: Now)],
        };
        view.RecordingsByLogin["alpha"] =
        [
            Recording("r1", "alpha", RecordingState.Saved, Now.AddHours(-1)),
        ];

        await using BunitContext context = CreateContext(view);
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        cut.Find("[role='treeitem'][aria-level='1']").Click();

        cut.WaitForAssertion(() =>
        {
            var header = cut.Find("[role='treeitem'][aria-level='1']");
            Assert.Equal("false", header.GetAttribute("aria-expanded"));
            Assert.Equal(
                header.GetAttribute("aria-controls"),
                cut.Find("[role='tree'] [role='group']").GetAttribute("id"));
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

        await using BunitContext context = CreateContext(view);
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
            Assert.NotNull(cut.Find("[data-recording-id='r1'] [data-status='partial']"));
        });
    }

    [Fact]
    public async Task Day_headers_use_the_viewer_time_zone()
    {
        var view = new TestLibraryView
        {
            StreamerItems = [Streamer("alpha", "Alpha", recording: false, count: 1, last: Now)],
        };
        view.RecordingsByLogin["alpha"] =
        [
            Recording("r1", "alpha", RecordingState.Saved, Now.AddHours(-14)),
        ];

        await using BunitContext context = CreateContext(view, timeZoneId: "Asia/Tokyo");
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Assert.Equal("Today", cut.Find("[data-day-header]").TextContent);
    }

    [Fact]
    public async Task Main_layout_lights_the_recording_dot_and_warns_below_ten_percent()
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

        await using BunitContext context = CreateContext(view);
        IRenderedComponent<MainLayout> cut = context.Render<MainLayout>(parameters =>
            parameters.Add(component => component.Body, fragment =>
                fragment.AddMarkupContent(0, "<div data-test='body'>Body</div>")));

        Assert.NotNull(cut.Find("header a[href='/'] [data-rec-dot]"));
        Assert.Equal("low", cut.Find("[data-storage]").GetAttribute("data-storage"));
        Assert.Contains("5.0 GB free of 100.0 GB", cut.Find("[data-storage]").GetAttribute("title"));
    }

    [Theory]
    [InlineData(105.0, "width:25%")]
    [InlineData(null, "width:100%")]
    public async Task A_live_row_shows_its_resume_bar_or_the_full_live_bar(double? resume, string width)
    {
        await using BunitContext context = CreateContext(new TestLibraryView());
        RecordingView live = Recording("r1", "ann", RecordingState.Recording, Now) with { ResumeSec = resume };

        IRenderedComponent<RecordingRow> cut = context.Render<RecordingRow>(parameters => parameters
            .Add(component => component.Recording, live));

        Assert.Equal(width, cut.Find("[data-progress] i").GetAttribute("style"));
    }

    [Fact]
    public async Task A_paused_streamer_shows_paused_without_a_live_ring()
    {
        await using BunitContext context = CreateContext(new TestLibraryView());
        StreamerView paused = Streamer(
            "ann", "Ann", recording: false, count: 2, last: Now, paused: true);

        IRenderedComponent<StreamerGroup> cut = context.Render<StreamerGroup>(parameters => parameters
            .Add(component => component.Streamer, paused)
            .Add(component => component.Recordings, [Recording("r1", "ann", RecordingState.Saved, Now)])
            .Add(component => component.Expanded, true)
            .Add(component => component.Now, Now));

        Assert.Contains("Paused", cut.Find("[data-group-meta]").TextContent);
        Assert.Empty(cut.FindAll("[data-live-ring]"));
    }

    [Fact]
    public async Task Unknown_recording_renders_a_message_and_link_back()
    {
        var view = new TestLibraryView { IsLoaded = true };

        await using BunitContext context = CreateContext(view);
        IRenderedComponent<Home> cut = context.Render<Home>(parameters =>
            parameters.Add(component => component.RecordingId, "missing"));

        Assert.NotNull(cut.Find("[data-unknown-recording]"));
        Assert.Contains("Recording not found", cut.Find("[data-unknown-recording]").TextContent);
        Assert.Equal("/", cut.Find("[data-unknown-recording] a").GetAttribute("href"));
    }

    [Fact]
    public async Task Known_recording_renders_the_selected_title()
    {
        var view = new TestLibraryView { IsLoaded = true };
        view.FindById["r1"] = Recording("r1", "alpha", RecordingState.Saved, Now.AddHours(-1));

        await using BunitContext context = CreateContext(view);
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
        context.Services.AddSingleton<IExportService>(new TestExportService());
        context.Services.AddSingleton<IChannelsView>(new TestChannelsView());
        context.Services.AddSingleton<IRecordingAdmin>(new TestRecordingAdmin());
        context.Services.AddSingleton<IProgressStore>(new TestProgressStore());
        context.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        context.Services.AddSingleton<ILibraryView>(view);
        context.Services.AddScoped<UiPreferences>();
        // MainLayout hosts the MudBlazor shell, whose links come from configuration.
        context.Services.AddMudServices();
        context.Services.AddSingleton<MudBlazor.IBrowserViewportService>(new TestBrowserViewport());
        context.Services.AddSingleton(Options.Create(new LinksOptions
        {
            GitHub = "https://github.com/example/streamkeepr",
            Twitch = "https://twitch.example/",
        }));
        // MainLayout hosts the top-bar Twitch control.
        context.Services.AddScoped<AdminSessionState>();
        context.AddAuthorization().SetNotAuthorized();

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
