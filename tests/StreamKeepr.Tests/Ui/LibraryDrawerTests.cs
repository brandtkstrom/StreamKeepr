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
    public async Task Renders_channels_and_the_picked_channels_rows_as_real_links()
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

        Assert.Equal("2 channels, 3 recordings", cut.Find("[data-library-counts]").TextContent.Trim());
        var channels = cut.FindAll("[role='option']");
        Assert.Equal(2, channels.Count);
        Assert.Contains("Live Star", channels[0].TextContent);
        Assert.Equal("Recording now", channels[0].QuerySelector("[data-channel-meta]")!.TextContent);
        Assert.Contains("Idle Star", channels[1].TextContent);
        // The day of the channel's latest recording.
        Assert.Equal(Format.DayHeader(Now.AddDays(-3), Now, TimeZoneInfo.Utc), channels[1].QuerySelector("[data-channel-meta]")!.TextContent);

        Open(cut, "live");
        Assert.Equal(2, cut.FindAll("a[data-recording-id]").Count);
        Assert.Equal("/r/r1?autoplay=1", cut.Find("a[data-recording-id='r1']").GetAttribute("href"));
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
            var avatar = cut.Find($"[data-library-channel='{login}'] [data-channel-color]");
            Assert.Equal(ChannelColor.For(login).ToString(), avatar.GetAttribute("data-channel-color"));
        }

        Assert.NotNull(cut.Find("[data-library-channel='live'] [data-live-ring]"));
        Assert.Empty(cut.FindAll("[data-library-channel='removed_one'] [data-live-ring]"));
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_the_rail_names_each_avatar_in_a_tooltip(bool rail)
    {
        var view = new TestLibraryView
        {
            StreamerItems = [Streamer("live", "Live Star", recording: true, count: 1, last: Now)],
        };
        view.RecordingsByLogin["live"] = [Recording("r1", "live", RecordingState.Recording, Now)];
        await using BunitContext context = CreateContext(view);

        var cut = context.Render<CascadingValue<bool>>(parameters => parameters
            .Add(component => component.Name, "LibraryRail")
            .Add(component => component.Value, rail)
            .AddChildContent<LibraryDrawer>());

        Assert.Equal(rail ? 1 : 0, cut.FindAll("[data-library-channel='live'] [data-rail-name]").Count);
        Assert.Null(cut.Find("[data-library-channel='live']").GetAttribute("title"));
    }

    [Theory]
    [InlineData(10L * 1024 * 1024 * 1024, "ok")]
    [InlineData(10L * 1024 * 1024 * 1024 - 1, "low")]
    public async Task The_warning_starts_just_below_ten_percent_free(long free, string expected)
    {
        var view = new TestLibraryView { Storage = new StorageView(free, 100L * 1024 * 1024 * 1024) };
        await using BunitContext context = CreateContext(view);
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Assert.Equal(expected, cut.Find("[data-storage]").GetAttribute("data-storage"));
    }

    [Fact]
    public async Task The_updates_paused_note_is_a_polite_status_with_a_circuit_free_retry()
    {
        await using BunitContext context = CreateContext(new TestLibraryView());
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        var note = cut.Find("[data-updates-paused]");
        Assert.Equal("status", note.GetAttribute("role"));
        Assert.Contains("Live updates paused", note.TextContent);
        // reconnect.js serves it by this hook, because no Blazor event fires while the
        // circuit is down, which is when the note shows.
        Assert.Equal("Retry", note.QuerySelector("button[data-reconnect-retry]")!.TextContent.Trim());
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
    public async Task In_the_rail_the_footer_is_a_storage_button()
    {
        const long GiB = 1024L * 1024 * 1024;
        var view = new TestLibraryView { Storage = new StorageView(5 * GiB, 100 * GiB) };
        await using BunitContext context = CreateContext(view);

        var cut = context.Render<CascadingValue<bool>>(parameters => parameters
            .Add(component => component.Name, "LibraryRail")
            .Add(component => component.Value, true)
            .AddChildContent<LibraryDrawer>());

        Assert.Empty(cut.FindAll("[data-storage]"));
        var button = cut.Find("button[data-storage-rail]");
        Assert.Equal("low", button.GetAttribute("data-storage-rail"));
        Assert.Equal("Recording disk: 5.0 GB free of 100.0 GB", button.GetAttribute("aria-label"));
        Assert.Equal("/", cut.Find("a[aria-label='Home']").GetAttribute("href"));
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
        Open(cut, "alpha");

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
        Open(cut, "alpha");

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
        // A closed library follows the watched recording to its channel, so it must know both.
        view.FindById["r1"] = view.RecordingsByLogin["alpha"][0];
        view.FindById["r2"] = view.RecordingsByLogin["alpha"][1];

        await using BunitContext context = CreateContext(view);
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/r/r2");

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();
        Open(cut, "alpha");

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
        Open(cut, "alpha");

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
        // Wide, the recordings pane mirrors the channel skeletons; it is silent to AT.
        Assert.Single(cut.FindAll("[data-recordings-skeleton]"));
        Assert.Equal("true", cut.Find("[data-recordings-skeleton]").GetAttribute("aria-hidden"));
        Assert.Empty(cut.FindAll("[data-recordings-prompt]"));

        view.IsLoaded = true;
        view.RaiseChanged();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-library-skeleton]"));
            Assert.Empty(cut.FindAll("[data-recordings-skeleton]"));
            Assert.Single(cut.FindAll("[data-library-empty]"));
            Assert.Contains("No recordings yet", cut.Find("[data-library-empty]").TextContent);
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
        Open(cut, "alpha");

        IRenderedComponent<RecordingRow> row = cut.FindComponent<RecordingRow>();
        IRenderedComponent<ChannelOption> option = cut.FindComponent<ChannelOption>();
        int rowRenders = 0;
        int optionRenders = 0;
        row.OnAfterRender += (_, _) => rowRenders++;
        option.OnAfterRender += (_, _) => optionRenders++;

        // Deliver the opening render's lifecycle before measuring the reused update.
        await cut.InvokeAsync(view.RaiseChanged);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-recording-id='r1']")));
        rowRenders = 0;
        optionRenders = 0;

        // The shell re-renders the drawer on every preference change; unchanged channels and
        // rows must not render again, or rapid input elsewhere (a slider) loses keys.
        await cut.InvokeAsync(view.RaiseChanged);

        Assert.Equal(0, rowRenders);
        Assert.Equal(0, optionRenders);

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
        Open(cut, "alpha");

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

        Assert.NotNull(cut.Find("header [data-recording-tally] [data-rec-dot]"));
        // Md and up the library starts as the rail: the disk is its storage button.
        Assert.Equal("rail", cut.Find("nav[aria-label='Library']").GetAttribute("data-drawer"));
        Assert.Equal("low", cut.Find("[data-storage-rail]").GetAttribute("data-storage-rail"));

        // The storage button opens the library over the page, with the full meter.
        await cut.Find("button[data-storage-rail]").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-shell][data-library-open]")));
        Assert.Equal("low", cut.Find("[data-storage]").GetAttribute("data-storage"));
        Assert.Contains("5.0 GB free of 100.0 GB", cut.Find("[data-storage]").GetAttribute("title"));
    }

    [Fact]
    public async Task A_rail_avatar_opens_the_library_on_its_channel()
    {
        var view = new TestLibraryView
        {
            StreamerItems =
            [
                Streamer("alpha", "Alpha", recording: false, count: 1, last: Now),
                Streamer("beta", "Beta", recording: false, count: 1, last: Now.AddDays(-1)),
            ],
        };
        view.RecordingsByLogin["alpha"] = [Recording("r1", "alpha", RecordingState.Saved, Now)];
        view.RecordingsByLogin["beta"] = [Recording("r2", "beta", RecordingState.Saved, Now.AddDays(-1))];
        await using BunitContext context = CreateContext(view);
        IRenderedComponent<MainLayout> cut = context.Render<MainLayout>(parameters =>
            parameters.Add(component => component.Body, fragment =>
                fragment.AddMarkupContent(0, "<div data-test='body'>Body</div>")));

        cut.Find("[data-library-channel='beta']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("[data-shell][data-library-open]"));
            Assert.Equal("expanded", cut.Find("nav[aria-label='Library']").GetAttribute("data-drawer"));
            Assert.Single(cut.FindAll("[data-recordings-column='beta']"));
        });
        // Opening is not remembered.
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "skPrefs.writeUi");
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
    public async Task A_saved_row_with_a_zero_resume_shows_no_resume_bar()
    {
        await using BunitContext context = CreateContext(new TestLibraryView());
        RecordingView saved = Recording("r1", "ann", RecordingState.Saved, Now) with { ResumeSec = 0 };

        IRenderedComponent<RecordingRow> cut = context.Render<RecordingRow>(parameters => parameters
            .Add(component => component.Recording, saved));

        Assert.Empty(cut.FindAll("[data-progress]"));
    }

    [Fact]
    public async Task A_paused_streamer_shows_paused_without_a_live_ring()
    {
        var view = new TestLibraryView
        {
            StreamerItems = [Streamer("ann", "Ann", recording: false, count: 1, last: Now, paused: true)],
        };
        view.RecordingsByLogin["ann"] = [Recording("r1", "ann", RecordingState.Saved, Now)];
        await using BunitContext context = CreateContext(view);

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Assert.Equal("Paused", cut.Find("[data-library-channel='ann'] [data-channel-meta]").TextContent);
        Assert.Empty(cut.FindAll("[data-live-ring]"));
    }

    [Fact]
    public async Task Unknown_recording_renders_a_message_and_link_back()
    {
        var view = new TestLibraryView { IsLoaded = true };

        await using BunitContext context = CreateContext(view);
        IRenderedComponent<Home> cut = context.Render<Home>(parameters =>
            parameters.Add(component => component.RecordingId, "missing"));

        AngleSharp.Dom.IElement state = cut.Find("[data-unknown-recording]");
        Assert.Equal("Recording not found", state.QuerySelector("h1")!.TextContent);
        Assert.Contains("That recording is not in the library.", state.TextContent);
        Assert.Equal("search", state.GetAttribute("data-empty-icon"));
        Assert.Equal("/", state.Named("a", "Back to library").GetAttribute("href"));
    }

    [Fact]
    public async Task An_empty_library_offers_to_add_a_channel_when_signed_in()
    {
        var view = new TestLibraryView { IsLoaded = true };

        await using BunitContext context = CreateContext(view, signedIn: true);
        IRenderedComponent<Home> cut = context.Render<Home>();

        AngleSharp.Dom.IElement state = cut.Find("[data-library-empty-home]");
        Assert.Equal("No recordings yet", state.QuerySelector("h2")!.TextContent);
        Assert.Equal("video_library", state.GetAttribute("data-empty-icon"));
        Assert.Contains("Stream Keepr records a channel each time it goes live.", state.TextContent);
        AngleSharp.Dom.IElement action = state.Named("a", "Add a channel");
        Assert.Equal("/settings#channels", action.GetAttribute("href"));
        Assert.False(state.HasNamed("a", "Connect with Twitch"));
    }

    [Fact]
    public async Task An_empty_library_offers_Twitch_when_signed_out()
    {
        var view = new TestLibraryView { IsLoaded = true };

        await using BunitContext context = CreateContext(view);
        IRenderedComponent<Home> cut = context.Render<Home>();

        AngleSharp.Dom.IElement state = cut.Find("[data-library-empty-home]");
        AngleSharp.Dom.IElement action = state.Named("a", "Connect with Twitch");
        Assert.Equal("/api/auth/twitch/login?returnUrl=%2F", action.GetAttribute("href"));
        // The shared hook gives it the top bar's "Waiting for Twitch…" state on click.
        Assert.True(action.HasAttribute("data-twitch-connect"));
        Assert.False(state.HasNamed("a", "Add a channel"));
    }

    [Fact]
    public async Task The_home_shows_no_empty_state_while_loading_or_with_recordings()
    {
        var view = new TestLibraryView { IsLoaded = false };

        await using BunitContext context = CreateContext(view);
        IRenderedComponent<Home> cut = context.Render<Home>();
        Assert.Empty(cut.FindAll("[data-library-empty-home]"));

        view.StreamerItems = [Streamer("live", "Live Star", recording: true, count: 1, last: Now)];
        view.IsLoaded = true;
        view.RaiseChanged();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-library-empty-home]")));

        view.StreamerItems = [];
        view.RaiseChanged();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-library-empty-home]")));
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

    // Rows show in the picked channel's pane.
    private static void Open(IRenderedComponent<LibraryDrawer> cut, string login) =>
        cut.Find($"[data-library-channel='{login}']").Click();

    private static BunitContext CreateContext(
        TestLibraryView view,
        string? timeZoneId = null,
        bool signedIn = false)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();

        var store = new TestClientStateStore();
        if (timeZoneId is not null)
            store.Cookies[ViewerClock.CookieName] = timeZoneId;

        context.Services.AddSingleton<IClientStateStore>(store);
        context.Services.AddScoped<ViewerClock>();
        context.Services.AddScoped<PlayerBridge>();
        context.Services.AddSingleton<IExportService>(new TestExportService());
        context.Services.AddSingleton<IChannelsView>(new TestChannelsView());
        context.Services.AddSingleton<IRecordingAdmin>(new TestRecordingAdmin());
        context.Services.AddSingleton<IProgressStore>(new TestProgressStore());
        context.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        context.Services.AddSingleton<ILibraryView>(view);
        context.Services.AddScoped<UiPreferences>();
        // MainLayout hosts the MudBlazor shell, whose links come from configuration.
        context.Services.AddStreamKeeprMudServices();
        context.Services.AddSingleton<MudBlazor.IBrowserViewportService>(new TestBrowserViewport());
        context.Services.AddSingleton(Options.Create(new LinksOptions
        {
            GitHub = "https://github.com/example/streamkeepr",
            Twitch = "https://twitch.example/",
        }));
        // MainLayout hosts the top-bar Twitch control.
        context.Services.AddScoped<AdminSessionState>();
        BunitAuthorizationContext authorization = context.AddAuthorization();
        if (signedIn)
            authorization.SetAuthorized("mock_admin");
        else
            authorization.SetNotAuthorized();

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

    // A stand-in for the real merge: only the two state sets Home reads, newest first.
    // LibraryViewTests pin the real filter and order.
    public IReadOnlyList<RecordingView> AllRecordings(LibraryQuery query) =>
    [
        .. RecordingsByLogin.Values
            .SelectMany(recordings => recordings)
            .Where(recording => query.State switch
            {
                "recording" => recording.State.IsOpen(),
                "saved" => recording.State is RecordingState.Saved or RecordingState.Partial,
                _ => true,
            })
            .OrderByDescending(recording => recording.StartedAt)
            .ThenBy(recording => recording.Id, StringComparer.Ordinal),
    ];

    public RecordingView? Find(string id) => FindById.GetValueOrDefault(id);

    public void RaiseChanged() => Changed?.Invoke();

    public void Dispose()
    {
    }
}
