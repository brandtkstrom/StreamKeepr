using Bunit.TestDoubles;
using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Components.Features.Details;
using StreamKeepr.Components.Features.Player;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class RecordingDetailsTests
{
    private const string Module = "./Components/Features/Details/RecordingDetails.razor.js";
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_title_block_shows_the_title_channel_facts_and_file()
    {
        await using BunitContext context = CreateContext(out _, timeZoneId: "Asia/Tokyo");

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        // The title block sits on the page, outside any panel: nothing collapses it.
        Assert.Empty(cut.FindAll("[data-recording-details] [data-collapsible-toggle]"));

        Assert.Equal("Title", cut.Find("[data-details-title]").TextContent);
        Assert.Equal("ann", cut.Find("[data-details-streamer]").TextContent);
        Assert.Equal("Food", cut.Find("[data-details-category]").TextContent);
        Assert.Equal("Wed, Nov 11, 2026", cut.Find("[data-details-start]").TextContent.Trim());
        Assert.Equal("9:00 PM – 11:00 PM", cut.Find("[data-details-time]").TextContent.Trim());
        Assert.Equal("2h 14m", cut.Find("[data-details-duration]").TextContent.Trim());
        Assert.Equal("1 chapter", cut.Find("[data-details-chapters]").TextContent.Trim());
        Assert.Equal("9.8 GB", cut.Find("[data-details-size]").TextContent);
        Assert.Equal("1,342 segments", cut.Find("[data-details-segments]").TextContent.Trim());
        Assert.Equal("3 parts", cut.Find("[data-details-parts]").TextContent.Trim());
        Assert.Single(cut.FindAll("button[aria-label='More actions']"));
    }

    [Fact]
    public async Task The_title_is_measured_after_render_and_when_the_recording_changes()
    {
        await using BunitContext context = CreateContext(out BunitJSModuleInterop module);

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());
        cut.WaitForAssertion(() => Assert.Contains(
            module.Invocations,
            invocation => invocation.Identifier == "watchTitle"));

        int before = module.Invocations.Count(invocation => invocation.Identifier == "watchTitle");
        cut.Render(parameters => parameters.Add(
            component => component.Recording,
            Recording() with { Title = "A much longer recording title" }));

        cut.WaitForAssertion(() => Assert.Equal(
            before + 1,
            module.Invocations.Count(invocation => invocation.Identifier == "watchTitle")));
    }

    [Fact]
    public async Task The_chapters_panel_is_expanded_by_default_and_counts_its_chapters()
    {
        await using BunitContext context = CreateContext(out _);

        IRenderedComponent<ChaptersPanel> cut = RenderPanel(context, Recording(chapters:
        [
            new Chapter(60, "Intro", ""),
            new Chapter(3733, "Celeste", "game"),
        ]));

        Assert.Equal("true", cut.Find("[data-chapters-panel]").GetAttribute("data-expanded"));
        var toggle = cut.Find("[data-chapters-panel] [data-collapsible-toggle]");
        Assert.Equal("true", toggle.GetAttribute("aria-expanded"));
        Assert.Equal("Chapters", toggle.QuerySelector("h2")!.TextContent.Trim());
        Assert.Equal("2 chapters", cut.Find("[data-chapter-count]").TextContent.Trim());
        Assert.Equal(2, cut.FindAll("[data-chapter]").Count);
    }

    [Fact]
    public async Task Collapsed_state_comes_from_sk_ui()
    {
        await using BunitContext context = CreateContext(
            out _,
            detailsCookie: "{\"v\":1,\"details\":false}");

        IRenderedComponent<ChaptersPanel> cut = RenderPanel(context, Recording());

        Assert.Equal("false", cut.Find("[data-chapters-panel]").GetAttribute("data-expanded"));
        var toggle = cut.Find("[data-chapters-panel] [data-collapsible-toggle]");
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
        Assert.True(cut.Find($"#{toggle.GetAttribute("aria-controls")}").HasAttribute("inert"));
        Assert.Equal("1 chapter", cut.Find("[data-chapter-count]").TextContent.Trim());
    }

    [Fact]
    public async Task Toggling_updates_the_preference_and_persists_sk_ui()
    {
        await using BunitContext context = CreateContext(out _);
        UiPreferences preferences = context.Services.GetRequiredService<UiPreferences>();
        IRenderedComponent<ChaptersPanel> cut = RenderPanel(context, Recording());

        cut.Find("[data-chapters-panel] [data-collapsible-toggle]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.False(preferences.DetailsOpen);
            Assert.Equal("false", cut.Find("[data-chapters-panel]").GetAttribute("data-expanded"));
        });
        Assert.Contains(
            context.JSInterop.Invocations,
            invocation => invocation.Identifier == "skPrefs.writeUi");
    }

    [Theory]
    [InlineData(RecordingState.Recording)]
    [InlineData(RecordingState.Reconnecting)]
    public async Task Size_is_hidden_while_the_recording_is_in_progress(RecordingState state)
    {
        await using BunitContext context = CreateContext(out _);

        IRenderedComponent<RecordingDetails> cut = Render(
            context,
            Recording(state: state, sizeBytes: 10_522_669_875));

        Assert.Empty(cut.FindAll("[data-details-size]"));
        Assert.Empty(cut.FindAll("[data-details-segments]"));
        Assert.Empty(cut.FindAll("[data-details-parts]"));
        Assert.Contains("Elapsed", cut.Find("[data-details-facts]").TextContent);
    }

    [Fact]
    public async Task A_missing_category_reads_no_category()
    {
        await using BunitContext context = CreateContext(out _);

        IRenderedComponent<RecordingDetails> cut = Render(
            context,
            Recording(category: null));

        Assert.Equal("No category", cut.Find("[data-details-category]").TextContent);
    }

    [Fact]
    public async Task The_channel_byline_links_to_the_channels_twitch_page()
    {
        await using BunitContext context = CreateContext(out _);

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        var link = cut.Find("[data-details-channel]");
        Assert.Equal("https://www.twitch.tv/ann", link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Equal("noopener noreferrer", link.GetAttribute("rel"));
        Assert.Equal(
            "ann",
            link.QuerySelector("[data-details-streamer]")!.TextContent.Trim());
        Assert.Equal(
            "on Twitch",
            link.QuerySelector("[data-details-channel-where]")!.TextContent.Trim());
        Assert.Contains(
            "(opens in a new tab)",
            link.QuerySelector("[data-details-channel-hint]")!.TextContent);
        Assert.NotNull(link.QuerySelector("[data-details-avatar]"));
    }

    [Fact]
    public async Task The_streamer_display_name_replaces_the_login_in_the_byline()
    {
        await using BunitContext context = CreateContext(out _);

        IRenderedComponent<RecordingDetails> cut = context.Render<RecordingDetails>(parameters => parameters
            .Add(component => component.Recording, Recording())
            .Add(component => component.Streamer, new StreamerView(
                "ann", "Ann Smith", null, false, false, false, 1, null)));

        Assert.Equal("Ann Smith", cut.Find("[data-details-streamer]").TextContent.Trim());
        Assert.Equal(
            "https://www.twitch.tv/ann",
            cut.Find("[data-details-channel]").GetAttribute("href"));
    }

    [Theory]
    [InlineData(0, "No chapters")]
    [InlineData(1, "1 chapter")]
    [InlineData(2, "2 chapters")]
    public async Task The_chapter_count_matches_the_filtered_chapters_panel(
        int count,
        string label)
    {
        await using BunitContext context = CreateContext(out _);
        Chapter[] chapters = Enumerable.Range(0, count)
            .Select(index => new Chapter(index * 60, $"Chapter {index}", "Food"))
            .ToArray();

        IRenderedComponent<RecordingDetails> cut = Render(
            context,
            Recording(chapters: chapters));

        Assert.Equal(label, cut.Find("[data-details-chapters]").TextContent.Trim());
    }

    [Fact]
    public async Task A_live_recording_reads_elapsed_and_now()
    {
        await using BunitContext context = CreateContext(out _);

        IRenderedComponent<RecordingDetails> cut = Render(
            context,
            Recording(state: RecordingState.Recording));

        Assert.Contains("Elapsed", cut.Find("[data-details-facts]").TextContent);
        Assert.Equal("12:00 PM – now", cut.Find("[data-details-time]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-details-size]"));
    }

    [Fact]
    public async Task A_time_range_across_midnight_carries_the_end_date()
    {
        await using BunitContext context = CreateContext(out _);
        RecordingView recording = Recording() with
        {
            StartedAt = new DateTimeOffset(2026, 11, 11, 23, 30, 0, TimeSpan.Zero),
            EndedAt = new DateTimeOffset(2026, 11, 12, 1, 0, 0, TimeSpan.Zero),
        };

        IRenderedComponent<RecordingDetails> cut = Render(context, recording);

        Assert.Equal(
            "11:30 PM – Nov 12, 1:00 AM",
            cut.Find("[data-details-time]").TextContent.Trim());
    }

    [Fact]
    public async Task A_missing_end_time_falls_back_to_the_duration()
    {
        await using BunitContext context = CreateContext(out _);
        RecordingView recording = Recording() with
        {
            EndedAt = null,
            DurationSec = 3600,
        };

        IRenderedComponent<RecordingDetails> cut = Render(context, recording);

        Assert.Equal("12:00 PM – 1:00 PM", cut.Find("[data-details-time]").TextContent.Trim());
    }

    [Fact]
    public async Task Chapters_are_buttons_with_an_accessible_name_and_seek_through_the_bridge()
    {
        await using BunitContext context = CreateContext(out _);
        var bridge = context.Services.GetRequiredService<PlayerBridge>();
        var seeks = new List<double>();
        bridge.Attach(seconds =>
        {
            seeks.Add(seconds);
            return ValueTask.CompletedTask;
        });

        IRenderedComponent<ChapterList> cut = RenderChapters(
            context,
            Recording(chapters:
            [
                new Chapter(3733, "Celeste", "game"),
                new Chapter(60, "Intro", ""),
            ]));

        var buttons = cut.FindAll("[data-chapter]");
        Assert.Equal(2, buttons.Count);
        Assert.Contains("Seek to ", buttons[0].TextContent);
        Assert.Contains("1:02:13", buttons[0].TextContent);
        Assert.Contains("Celeste", buttons[0].TextContent);
        Assert.Contains("game", buttons[0].TextContent);
        Assert.DoesNotContain("(", buttons[1].TextContent);

        buttons[0].Click();

        cut.WaitForAssertion(() => Assert.Equal([3733d], seeks));
    }

    [Fact]
    public async Task Chapter_offsets_use_invariant_culture_and_duplicate_offsets_do_not_collide()
    {
        await using BunitContext context = CreateContext(out _);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

            IRenderedComponent<ChapterList> cut = RenderChapters(
                context,
                Recording(chapters:
                [
                    new Chapter(2.5, "First", ""),
                    new Chapter(2.5, "Second", ""),
                ]));

            var buttons = cut.FindAll("[data-chapter]");
            Assert.Equal(2, buttons.Count);
            Assert.All(buttons, button => Assert.Equal("2.5", button.GetAttribute("data-chapter")));
            Assert.Equal(
                ["2.5:0", "2.5:1"],
                buttons.Select(button => button.GetAttribute("data-chapter-key")));
            Assert.Contains("First", buttons[0].TextContent);
            Assert.Contains("Second", buttons[1].TextContent);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Empty_and_invalid_chapters_produce_no_list()
    {
        await using BunitContext context = CreateContext(out _);

        IRenderedComponent<ChapterList> cut = RenderChapters(
            context,
            Recording(chapters:
            [
                new Chapter(double.NaN, "NaN", ""),
                new Chapter(-1, "Negative", ""),
                new Chapter(9000, "Past the end", ""),
            ]));

        Assert.Empty(cut.FindAll("[data-chapter]"));
        Assert.Empty(cut.FindAll("[role='group'][aria-label='Chapters']"));
        // No chapters, no panel: not an empty one.
        Assert.Empty(RenderPanel(context, Recording(chapters: [new Chapter(-1, "Negative", "")])).FindAll("[data-chapters-panel]"));
    }

    [Fact]
    public async Task The_start_time_re_renders_when_the_viewer_clock_changes()
    {
        await using BunitContext context = CreateContext(out _);
        ViewerClock clock = context.Services.GetRequiredService<ViewerClock>();
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        Assert.Equal("Wed, Nov 11, 2026", cut.Find("[data-details-start]").TextContent.Trim());
        Assert.Equal("12:00 PM – 2:00 PM", cut.Find("[data-details-time]").TextContent.Trim());

        clock.TrySetTimeZone("Asia/Tokyo");

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Wed, Nov 11, 2026", cut.Find("[data-details-start]").TextContent.Trim());
            Assert.Equal("9:00 PM – 11:00 PM", cut.Find("[data-details-time]").TextContent.Trim());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copy_playlist_builds_the_absolute_url_and_confirms_from_the_menu(bool signedIn)
    {
        await using BunitContext context = CreateContext(out BunitJSModuleInterop module, signedIn: signedIn);
        module.Setup<bool>("copyText", _ => true).SetResult(true);
        MudBlazor.ISnackbar snackbars = context.Services.GetRequiredService<MudBlazor.ISnackbar>();
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        // Copying needs no sign-in; the public More actions menu holds it.
        Assert.Empty(cut.FindAll("[data-requires-twitch] button[aria-label='More actions']"));

        MoreActions.Item(context, cut, "data-copy-playlist").Click();

        cut.WaitForAssertion(() =>
        {
            JSRuntimeInvocation copy = Assert.Single(
                module.Invocations,
                invocation => invocation.Identifier == "copyText");
            Assert.Equal("http://localhost/media/ann/r1/index.m3u8", copy.Arguments[0]);
            Assert.Equal("Playlist URL copied", snackbars.CurrentMessage());
        });
    }

    [Fact]
    public async Task A_rejected_clipboard_reports_the_failure_in_the_snackbar()
    {
        await using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        module.Setup<bool>("copyText", _ => true).SetResult(false);
        MudBlazor.ISnackbar snackbars = context.Services.GetRequiredService<MudBlazor.ISnackbar>();
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        MoreActions.Item(context, cut, "data-copy-playlist").Click();

        cut.WaitForAssertion(() =>
            Assert.Equal("Could not copy playlist URL", snackbars.CurrentMessage()));
    }

    private static IRenderedComponent<RecordingDetails> Render(
        BunitContext context,
        RecordingView recording) =>
        context.Render<RecordingDetails>(parameters =>
            parameters.Add(component => component.Recording, recording));

    private static IRenderedComponent<ChapterList> RenderChapters(BunitContext context, RecordingView recording) =>
        context.Render<ChapterList>(parameters => parameters.Add(component => component.Recording, recording));

    private static IRenderedComponent<ChaptersPanel> RenderPanel(BunitContext context, RecordingView recording) =>
        context.Render<ChaptersPanel>(parameters => parameters.Add(component => component.Recording, recording));

    private static BunitContext CreateContext(
        out BunitJSModuleInterop module,
        string? timeZoneId = null,
        string? detailsCookie = null,
        bool signedIn = false)
    {
        var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        module = context.JSInterop.SetupModule(Module);
        module.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();

        var store = new TestClientStateStore();
        if (timeZoneId is not null)
            store.Cookies[ViewerClock.CookieName] = timeZoneId;
        if (detailsCookie is not null)
            store.Cookies[UiPreferences.CookieName] = detailsCookie;

        context.Services.AddSingleton<IClientStateStore>(store);
        context.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        context.Services.AddScoped<ViewerClock>();
        context.Services.AddScoped<UiPreferences>();
        context.Services.AddScoped<PlayerBridge>();
        context.Services.AddSingleton<IExportService>(new TestExportService());
        context.Services.AddSingleton<IChannelsView>(new TestChannelsView());
        context.Services.AddSingleton<IRecordingAdmin>(new TestRecordingAdmin());
        BunitAuthorizationContext auth = context.AddAuthorization();
        if (signedIn)
            auth.SetAuthorized("mock_admin");
        else
            auth.SetNotAuthorized();
        return context;
    }

    private static RecordingView Recording(
        RecordingState state = RecordingState.Saved,
        long? sizeBytes = 10_522_669_875,
        string? category = "Food",
        IReadOnlyList<Chapter>? chapters = null) =>
        new(
            "r1",
            "ann",
            "Title",
            category,
            new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero),
            8047,
            state,
            SegmentCount: 1342,
            PartCount: 3,
            SizeBytes: sizeBytes,
            ThumbnailUrl: null,
            PlaylistUrl: "/media/ann/r1/index.m3u8",
            ResumeSec: null,
            Chapters: chapters ?? [new Chapter(3733, "Celeste", "game")]);
}
