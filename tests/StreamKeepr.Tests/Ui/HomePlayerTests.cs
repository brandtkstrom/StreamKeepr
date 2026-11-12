using Bunit.TestDoubles;
using StreamKeepr.Infrastructure.Storage;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
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
    public void A_known_recording_renders_the_player_in_the_player_slot()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<Home> cut = context.Render<Home>(p => p.Add(c => c.RecordingId, "r1"));

        Assert.NotNull(cut.Find("[data-slot='player'] [data-sk-player]"));
        Assert.NotNull(cut.Find("[data-slot='details'] [data-recording-details]"));
        Assert.Equal(["player", "stats", "details"], cut.FindAll("[data-slot]").Select(s => s.GetAttribute("data-slot")));
    }

    [Fact]
    public void The_library_page_has_no_player()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<Home> cut = context.Render<Home>();

        Assert.Empty(cut.FindAll("[data-sk-player]"));
        Assert.Empty(cut.FindAll("[data-sk-stats]"));
    }

    [Fact]
    public void The_stats_panel_renders_once_and_shares_the_open_state_with_the_player_button()
    {
        const string openCookie =
            """{"v":1,"drawer":"expanded","theater":false,"stats":true,"details":true,"collapsed":[]}""";
        using BunitContext context = CreateContext(out _, openCookie);

        IRenderedComponent<Home> cut = context.Render<Home>(p => p.Add(c => c.RecordingId, "r1"));

        Assert.Single(cut.FindAll("[data-slot='stats'] [data-sk-stats]"));
        Assert.Equal("true", cut.Find("[data-sk-stats]").GetAttribute("data-open"));
        Assert.Equal("true", cut.Find("[data-sk='stats-toggle']").GetAttribute("aria-checked"));
        Assert.False(cut.Find("[data-sk='stats-body']").HasAttribute("inert"));
        Assert.Equal("true", cut.Find("[data-sk='stats']").GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Changing_recording_keeps_the_same_player_instance()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        IRenderedComponent<Home> cut = context.Render<Home>(p => p.Add(c => c.RecordingId, "r1"));
        VideoPlayer before = cut.FindComponent<VideoPlayer>().Instance;

        cut.Render(p => p.Add(c => c.RecordingId, "r2"));

        Assert.Same(before, cut.FindComponent<VideoPlayer>().Instance);
        Assert.Single(module.Invocations, call => call.Identifier == "init");
        Assert.Single(module.Invocations, call => call.Identifier == "load");
    }

    [Fact]
    public void The_details_card_renders_once_and_survives_a_recording_change()
    {
        using BunitContext context = CreateContext(out _);
        IRenderedComponent<Home> cut = context.Render<Home>(p => p.Add(c => c.RecordingId, "r1"));
        RecordingDetails before = cut.FindComponents<RecordingDetails>().Single().Instance;

        Assert.Equal("First", cut.Find("[data-details-title]").TextContent);

        cut.Render(p => p.Add(c => c.RecordingId, "r2"));

        Assert.Same(before, cut.FindComponents<RecordingDetails>().Single().Instance);
        Assert.Single(cut.FindAll("[data-recording-details]"));
        Assert.Equal("Second", cut.Find("[data-details-title]").TextContent);
    }

    private static BunitContext CreateContext(out BunitJSModuleInterop module, string? statsCookie = null)
    {
        var view = new TestLibraryView();
        view.FindById["r1"] = VideoPlayerTests.Recording("r1", "First");
        view.FindById["r2"] = VideoPlayerTests.Recording("r2", "Second");

        var context = new BunitContext();
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
        context.Services.AddScoped<SnackbarService>();
        context.Services.AddScoped<PlayerBridge>();
        context.Services.AddSingleton<IExportService>(new TestExportService());
        context.Services.AddSingleton<IChannelsView>(new TestChannelsView());
        context.AddAuthorization().SetNotAuthorized();
        context.Services.AddSingleton<IProgressStore>(new TestProgressStore());
        context.Services.AddSingleton<ILibraryView>(view);
        return context;
    }
}
