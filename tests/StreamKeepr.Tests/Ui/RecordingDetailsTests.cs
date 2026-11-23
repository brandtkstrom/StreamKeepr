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
    public async Task The_title_block_shows_the_title_streamer_category_start_counts_and_size()
    {
        await using BunitContext context = CreateContext(out _, timeZoneId: "Asia/Tokyo");

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        // The title block sits on the page, outside any panel: nothing collapses it.
        Assert.Empty(cut.FindAll("[data-recording-details] [data-collapsible-toggle]"));

        Assert.Equal("Title", cut.Find("[data-details-title]").TextContent);
        Assert.Equal("ann", cut.Find("[data-details-streamer]").TextContent);
        Assert.Equal("Food", cut.Find("[data-details-category]").TextContent);
        Assert.Equal("Wed, Nov 11, 2026 · 9:00 PM", cut.Find("[data-details-start]").TextContent);
        Assert.Equal("1,342 segments", cut.Find("[data-details-segments]").TextContent);
        Assert.Equal("3 parts", cut.Find("[data-details-parts]").TextContent);
        Assert.Equal("9.8 GB", cut.Find("[data-details-size]").TextContent);
        Assert.Single(cut.FindAll("button[aria-label='More actions']"));
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
        Assert.DoesNotContain("GB", cut.Find("[data-details-counts]").TextContent);
        Assert.Contains("1,342 segments", cut.Find("[data-details-counts]").TextContent);
        Assert.Contains("3 parts", cut.Find("[data-details-counts]").TextContent);
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

        Assert.Equal("Wed, Nov 11, 2026 · 12:00 PM", cut.Find("[data-details-start]").TextContent);

        clock.TrySetTimeZone("Asia/Tokyo");

        cut.WaitForAssertion(() => Assert.Equal(
            "Wed, Nov 11, 2026 · 9:00 PM",
            cut.Find("[data-details-start]").TextContent));
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
