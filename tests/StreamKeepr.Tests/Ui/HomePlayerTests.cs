using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using StreamKeepr.Configuration;
using StreamKeepr.Components.Layout;
using StreamKeepr.Infrastructure.Storage;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Components.Features.Details;
using StreamKeepr.Components.Features.Player;
using StreamKeepr.Components.Pages;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class HomePlayerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_known_recording_renders_the_player_in_the_player_slot()
    {
        await using BunitContext context = CreateContext(out _);

        IRenderedComponent<Home> cut = context.Render<Home>(p => p.Add(c => c.RecordingId, "r1"));

        Assert.NotNull(cut.Find("[data-slot='player'] [data-sk-player]"));
        Assert.NotNull(cut.Find("[data-slot='details'] [data-recording-details]"));
        Assert.Equal(["player", "details", "viewers"], cut.FindAll("[data-slot]").Select(s => s.GetAttribute("data-slot")));
    }

    [Fact]
    public async Task The_library_page_has_no_player()
    {
        await using BunitContext context = CreateContext(out _);

        IRenderedComponent<Home> cut = context.Render<Home>();

        Assert.Empty(cut.FindAll("[data-sk-player]"));
        Assert.Empty(cut.FindAll("[data-sk-stats]"));
    }

    [Fact]
    public async Task The_library_page_clears_theater_when_no_recording_is_selected()
    {
        const string theaterCookie =
            """{"v":1,"drawer":"expanded","theater":true,"stats":false,"details":true,"collapsed":[]}""";
        await using BunitContext context = CreateContext(out _, theaterCookie);
        UiPreferences preferences = context.Services.GetRequiredService<UiPreferences>();

        IRenderedComponent<Home> cut = context.Render<Home>(p => p.Add(c => c.RecordingId, "r1"));
        Assert.True(preferences.Theater);

        cut.Render(p => p.Add(c => c.RecordingId, (string?)null));

        Assert.False(preferences.Theater);
    }

    [Fact]
    public async Task The_statistics_overlay_renders_once_inside_the_player_and_shares_its_open_state()
    {
        const string openCookie =
            """{"v":1,"drawer":"expanded","theater":false,"stats":true,"details":true,"collapsed":[]}""";
        await using BunitContext context = CreateContext(out _, openCookie);

        IRenderedComponent<Home> cut = context.Render<Home>(p => p.Add(c => c.RecordingId, "r1"));

        Assert.Single(cut.FindAll("[data-sk-stats]"));
        Assert.Single(cut.FindAll("[data-slot='player'] [data-sk-player] [data-sk-stats]"));
        Assert.Equal("true", cut.Find("[data-sk-stats]").GetAttribute("data-open"));
        Assert.False(cut.Find("[data-sk-stats]").HasAttribute("hidden"));
        Assert.Equal("true", cut.Find("[data-sk='stats']").GetAttribute("aria-pressed"));
        // The viewer chart left the stats: it is the Viewers panel under the title block.
        Assert.Single(cut.FindAll("[data-slot='viewers'] [data-viewers-panel] [data-sk='viewer-chart']"));
    }

    [Fact]
    public async Task Changing_recording_keeps_the_same_player_instance()
    {
        await using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        IRenderedComponent<Home> cut = context.Render<Home>(p => p.Add(c => c.RecordingId, "r1"));
        VideoPlayer before = cut.FindComponent<VideoPlayer>().Instance;

        cut.Render(p => p.Add(c => c.RecordingId, "r2"));

        Assert.Same(before, cut.FindComponent<VideoPlayer>().Instance);
        Assert.Single(module.Invocations, call => call.Identifier == "init");
        Assert.Single(module.Invocations, call => call.Identifier == "load");
    }

    [Fact]
    public async Task The_details_card_renders_once_and_survives_a_recording_change()
    {
        await using BunitContext context = CreateContext(out _);
        IRenderedComponent<Home> cut = context.Render<Home>(p => p.Add(c => c.RecordingId, "r1"));
        RecordingDetails before = cut.FindComponents<RecordingDetails>().Single().Instance;

        Assert.Equal("First", cut.Find("[data-details-title]").TextContent);

        cut.Render(p => p.Add(c => c.RecordingId, "r2"));

        Assert.Same(before, cut.FindComponents<RecordingDetails>().Single().Instance);
        Assert.Single(cut.FindAll("[data-recording-details]"));
        Assert.Equal("Second", cut.Find("[data-details-title]").TextContent);
    }

    [Fact]
    public async Task Below_md_the_player_sits_above_the_title_block_and_the_chat_chapters_and_stats_tabs()
    {
        await using BunitContext context = CreateContext(out _, out TestLibraryView view);
        view.FindById["r1"] = VideoPlayerTests.Recording("r1", "First") with { HasChat = true };

        IRenderedComponent<Home> home = RenderShell(context, compact: true);

        Assert.NotNull(home.Find("[data-watch-top] [data-slot='player'] [data-sk-player]"));
        Assert.NotNull(home.Find("[data-watch-summary] [data-details-summary]"));
        Assert.Empty(home.FindAll("[data-watch-summary] [data-details-category]"));
        Assert.Empty(home.FindAll("[data-watch-summary] [data-details-counts]"));
        Assert.NotNull(home.Find("[data-watch-summary] [data-details-streamer]"));
        Assert.NotNull(home.Find("[data-watch-summary] [data-details-start]"));
        Assert.NotNull(home.Find("[data-watch-summary] [data-details-duration]"));
        Assert.Equal(
            ["Chat", "Chapters", "Stats"],
            home.FindAll("[role='tab']").Select(tab => tab.TextContent.Trim()));
        Assert.Empty(home.FindAll("[data-slot='details']"));
        Assert.Empty(home.FindAll("[data-slot='viewers']"));
        Assert.NotNull(home.Find("[role='tabpanel'] [data-sk-chat]"));
        // The export row follows the title block, outside the tabs; the chapters are a tab.
        Assert.NotNull(home.Find("[data-watch-summary] [data-details-extra]"));
        Assert.NotNull(home.Find("[role='tabpanel'] [data-chapter-list]"));
        Assert.Equal(
            "tab",
            home.Find("[role='tabpanel'] [data-sk-stats]").GetAttribute("data-chrome"));
        // The Stats tab ends with the viewer chart, with no Viewers panel header.
        Assert.NotNull(home.Find("[role='tabpanel'] [data-sk-stats] ~ [data-viewers-panel] [data-sk='viewer-chart']"));
        Assert.Empty(home.FindAll("[role='tabpanel'] [data-viewers-panel] [data-collapsible-toggle]"));
        // The Chat tab owns its disclosure: the pane has no header toggle of its own.
        Assert.Empty(home.FindAll("[data-sk='chat-toggle']"));
    }

    [Fact]
    public async Task Below_md_a_recording_without_chapters_has_no_chapters_tab()
    {
        await using BunitContext context = CreateContext(out _, out TestLibraryView view);
        view.FindById["r1"] = VideoPlayerTests.Recording("r1", "First") with { HasChat = true, Chapters = [] };

        IRenderedComponent<Home> home = RenderShell(context, compact: true);

        Assert.Equal(["Chat", "Stats"], home.FindAll("[role='tab']").Select(tab => tab.TextContent.Trim()));
    }

    [Fact]
    public async Task Md_and_up_the_title_block_is_followed_by_the_chapters_panel()
    {
        await using BunitContext context = CreateContext(out _, out TestLibraryView view);
        view.FindById["r1"] = VideoPlayerTests.Recording("r1", "First");

        IRenderedComponent<Home> home = RenderShell(context, compact: false);

        Assert.NotNull(home.Find("[data-slot='details'] [data-recording-details] [data-details-title]"));
        Assert.NotNull(home.Find("[data-slot='details'] [data-chapters-panel] [data-chapter]"));
        Assert.Empty(home.FindAll("[role='tab']"));
    }

    [Fact]
    public async Task Crossing_the_breakpoint_keeps_the_player_instance_and_swaps_the_tabs()
    {
        await using BunitContext context = CreateContext(out _, out TestLibraryView view);
        view.FindById["r1"] = VideoPlayerTests.Recording("r1", "First") with { HasChat = true };

        IRenderedComponent<CompactHost> host = context.Render<CompactHost>(parameters => parameters
            .Add(component => component.Compact, false));
        VideoPlayer before = host.FindComponent<VideoPlayer>().Instance;
        Assert.NotNull(host.Find("[data-slot='details'] [data-recording-details]"));

        host.Render(parameters => parameters.Add(component => component.Compact, true));

        Assert.Same(before, host.FindComponent<VideoPlayer>().Instance);
        Assert.NotNull(host.Find("[role='tab']"));
        Assert.Empty(host.FindAll("[data-slot='details']"));
    }

    private static IRenderedComponent<Home> RenderShell(BunitContext context, bool compact) =>
        context.Render<Home>(parameters => parameters
            .Add(component => component.RecordingId, "r1")
            .AddCascadingValue(AppShell.CompactCascade, compact));

    private static BunitContext CreateContext(out BunitJSModuleInterop module, string? statsCookie = null) =>
        CreateContext(out module, out _, statsCookie);

    internal static BunitContext CreateContext(
        out BunitJSModuleInterop module,
        out TestLibraryView view,
        string? statsCookie = null)
    {
        view = new TestLibraryView();
        view.FindById["r1"] = VideoPlayerTests.Recording("r1", "First");
        view.FindById["r2"] = VideoPlayerTests.Recording("r2", "Second");

        var context = new BunitContext();

        context.Services.AddStreamKeeprMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        module = context.JSInterop.SetupModule("./Components/Features/Player/VideoPlayer.razor.js");
        module.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./Components/Features/Player/StatsPanel.razor.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();

        var store = new TestClientStateStore();
        if (statsCookie is not null)
            store.Cookies[UiPreferences.CookieName] = statsCookie;
        context.Services.AddSingleton<IClientStateStore>(store);
        context.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        context.Services.AddScoped<ViewerClock>();
        context.Services.AddScoped<UiPreferences>();
        context.Services.AddScoped<PlayerBridge>();
        context.Services.AddSingleton<IExportService>(new TestExportService());
        context.Services.AddSingleton<IChannelsView>(new TestChannelsView());
        context.Services.AddSingleton<IRecordingAdmin>(new TestRecordingAdmin());
        context.AddAuthorization().SetNotAuthorized();
        context.Services.AddSingleton<IProgressStore>(new TestProgressStore());
        context.Services.AddSingleton<ILibraryView>(view);

        // Production defaults: chat capture and third-party emotes are off.
        context.Services.AddSingleton(Options.Create(new ChatOptions()));
        return context;
    }

    // Renders Home under a cascading compact flag so the test can cross Breakpoint.Md.
    private sealed class CompactHost : ComponentBase
    {
        [Parameter] public bool Compact { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<bool>>(0);
            builder.AddAttribute(1, nameof(CascadingValue<bool>.Name), AppShell.CompactCascade);
            builder.AddAttribute(2, nameof(CascadingValue<bool>.Value), Compact);
            builder.AddAttribute(3, nameof(CascadingValue<bool>.ChildContent), (RenderFragment)(content =>
            {
                content.OpenComponent<Home>(0);
                content.AddAttribute(1, nameof(Home.RecordingId), "r1");
                content.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
