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
    public void Expanded_card_shows_the_title_streamer_category_start_counts_and_size()
    {
        using BunitContext context = CreateContext(out _, timeZoneId: "Asia/Tokyo");

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        var section = cut.Find("[data-recording-details]");
        Assert.Equal("true", section.GetAttribute("data-expanded"));
        Assert.Equal("true", cut.Find(".sk-collapse-head").GetAttribute("aria-expanded"));
        Assert.Contains("Recording details", cut.Find(".sk-collapse-head").TextContent);
        Assert.Contains("Wed, Nov 11, 2026 · 9:00 PM", cut.Find("[data-details-date]").TextContent);

        Assert.Equal("Title", cut.Find("[data-details-title]").TextContent);
        Assert.Equal("ann", cut.Find("[data-details-streamer]").TextContent);
        Assert.Equal("Food", cut.Find("[data-details-category]").TextContent);
        Assert.Equal("Wed, Nov 11, 2026 · 9:00 PM", cut.Find("[data-details-start]").TextContent);
        Assert.Equal("1,342 segments", cut.Find("[data-details-segments]").TextContent);
        Assert.Equal("3 parts", cut.Find("[data-details-parts]").TextContent);
        Assert.Equal("9.8 GB", cut.Find("[data-details-size]").TextContent);
        Assert.Contains("Copy playlist URL", cut.Find("[data-copy-playlist]").TextContent);
    }

    [Fact]
    public void Collapsed_state_comes_from_sk_ui_and_the_header_keeps_the_start_date()
    {
        using BunitContext context = CreateContext(
            out _,
            detailsCookie: "{\"v\":1,\"details\":false}");

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        var section = cut.Find("[data-recording-details]");
        Assert.Equal("false", section.GetAttribute("data-expanded"));
        Assert.Equal("false", cut.Find(".sk-collapse-head").GetAttribute("aria-expanded"));
        Assert.True(cut.Find(".sk-collapse-body").HasAttribute("inert"));
        Assert.Contains("Wed, Nov 11, 2026 · 12:00 PM", cut.Find("[data-details-date]").TextContent);
    }

    [Fact]
    public void Toggling_updates_the_preference_and_persists_sk_ui()
    {
        using BunitContext context = CreateContext(out _);
        UiPreferences preferences = context.Services.GetRequiredService<UiPreferences>();
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        cut.Find(".sk-collapse-head").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.False(preferences.DetailsOpen);
            Assert.Equal("false", cut.Find("[data-recording-details]").GetAttribute("data-expanded"));
        });
        Assert.Contains(
            context.JSInterop.Invocations,
            invocation => invocation.Identifier == "skPrefs.writeUi");
    }

    [Theory]
    [InlineData(RecordingState.Recording)]
    [InlineData(RecordingState.Reconnecting)]
    public void Size_is_hidden_while_the_recording_is_in_progress(RecordingState state)
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<RecordingDetails> cut = Render(
            context,
            Recording(state: state, sizeBytes: 10_522_669_875));

        Assert.Empty(cut.FindAll("[data-details-size]"));
        Assert.DoesNotContain("GB", cut.Find("[data-details-counts]").TextContent);
        Assert.Contains("1,342 segments", cut.Find("[data-details-counts]").TextContent);
        Assert.Contains("3 parts", cut.Find("[data-details-counts]").TextContent);
    }

    [Fact]
    public void A_missing_category_reads_no_category()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<RecordingDetails> cut = Render(
            context,
            Recording(category: null));

        Assert.Equal("No category", cut.Find("[data-details-category]").TextContent);
    }

    [Fact]
    public async Task Chapters_are_buttons_with_an_accessible_name_and_seek_through_the_bridge()
    {
        using BunitContext context = CreateContext(out _);
        var bridge = context.Services.GetRequiredService<PlayerBridge>();
        var seeks = new List<double>();
        bridge.Attach(seconds =>
        {
            seeks.Add(seconds);
            return ValueTask.CompletedTask;
        });

        IRenderedComponent<RecordingDetails> cut = Render(
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
    public void Empty_and_invalid_chapters_produce_no_list()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<RecordingDetails> cut = Render(
            context,
            Recording(chapters:
            [
                new Chapter(double.NaN, "NaN", ""),
                new Chapter(-1, "Negative", ""),
                new Chapter(9000, "Past the end", ""),
            ]));

        Assert.Empty(cut.FindAll("[data-chapter]"));
        Assert.Empty(cut.FindAll(".sk-chapters"));
    }

    [Fact]
    public void The_start_time_re_renders_when_the_viewer_clock_changes()
    {
        using BunitContext context = CreateContext(out _);
        ViewerClock clock = context.Services.GetRequiredService<ViewerClock>();
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        Assert.Equal("Wed, Nov 11, 2026 · 12:00 PM", cut.Find("[data-details-start]").TextContent);

        clock.TrySetTimeZone("Asia/Tokyo");

        cut.WaitForAssertion(() => Assert.Equal(
            "Wed, Nov 11, 2026 · 9:00 PM",
            cut.Find("[data-details-start]").TextContent));
    }

    [Fact]
    public void Copy_playlist_builds_the_absolute_url_and_confirms_without_auth()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        context.AddAuthorization().SetNotAuthorized();
        module.Setup<bool>("copyText", _ => true).SetResult(true);
        SnackbarService snackbars = context.Services.GetRequiredService<SnackbarService>();
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        Assert.Empty(cut.FindAll(".sk-requires-twitch"));

        cut.Find("[data-copy-playlist]").Click();

        cut.WaitForAssertion(() =>
        {
            JSRuntimeInvocation copy = Assert.Single(
                module.Invocations,
                invocation => invocation.Identifier == "copyText");
            Assert.Equal("http://localhost/media/ann/r1/index.m3u8", copy.Arguments[0]);
            Assert.Equal("Playlist URL copied", snackbars.Current?.Text);
        });
    }

    [Fact]
    public void A_rejected_clipboard_reports_the_failure_in_the_snackbar()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        module.Setup<bool>("copyText", _ => true).SetResult(false);
        SnackbarService snackbars = context.Services.GetRequiredService<SnackbarService>();
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        cut.Find("[data-copy-playlist]").Click();

        cut.WaitForAssertion(() =>
            Assert.Equal("Could not copy playlist URL", snackbars.Current?.Text));
    }

    private static IRenderedComponent<RecordingDetails> Render(
        BunitContext context,
        RecordingView recording) =>
        context.Render<RecordingDetails>(parameters =>
            parameters.Add(component => component.Recording, recording));

    private static BunitContext CreateContext(
        out BunitJSModuleInterop module,
        string? timeZoneId = null,
        string? detailsCookie = null)
    {
        var context = new BunitContext();
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
        context.Services.AddScoped<SnackbarService>();
        context.Services.AddScoped<PlayerBridge>();
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
