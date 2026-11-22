using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Components.Features.Player;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class StatsPanelTests
{
    private const string Module = "./Components/Features/Player/StatsPanel.razor.js";
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    // Keep in step with HOOKS in StatsPanel.razor.js; the Vitest fixture mirrors these.
    private static readonly string[] Hooks =
    [
        "stats-close", "stats-body", "copy-stats",
        "value-bitrate", "spark-bitrate", "tip-bitrate", "table-bitrate", "rows-bitrate",
        "value-frame", "spark-frame", "tip-frame", "table-frame", "rows-frame",
        "value-bandwidth", "spark-bandwidth", "tip-bandwidth", "table-bandwidth", "rows-bandwidth",
        "value-buffer", "spark-buffer", "tip-buffer", "table-buffer", "rows-buffer",
        "kv-resolution", "kv-codecs", "kv-dropped", "kv-decoder",
        "kv-last-segment", "kv-segments", "kv-errors", "kv-playlist",
        "stats-stream", "stream-at", "stream-viewers",
    ];

    [Fact]
    public void Renders_the_overlay_skeleton_once_as_a_statistics_region()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p =>
            p.Add(c => c.Recording, Recording("r1")));

        foreach (string hook in Hooks)
            Assert.Single(cut.FindAll($"[data-sk='{hook}']"));

        // A <section> with an accessible name is a region, named by the card's title.
        var panel = cut.Find("section[data-sk-stats]");
        Assert.Equal("overlay", panel.GetAttribute("data-chrome"));
        Assert.Equal("false", panel.GetAttribute("data-open"));
        Assert.True(panel.HasAttribute("hidden"));
        var title = cut.Find("[data-stats-head] h2");
        Assert.Equal("Statistics", title.TextContent.Trim());
        Assert.Equal(title.GetAttribute("id"), panel.GetAttribute("aria-labelledby"));

        // The header's icon buttons: copy, then close.
        Assert.Equal(
            ["Copy stats", "Close statistics"],
            cut.FindAll("[data-stats-head] button").Select(b => b.GetAttribute("aria-label") ?? "").ToArray());

        Assert.Equal("/media/ann/r1/index.m3u8", cut.Find("[data-sk='kv-playlist']").TextContent.Trim());
        Assert.Equal("/media/ann/r1/index.m3u8", cut.Find("[data-sk='kv-playlist']").GetAttribute("title"));
        Assert.Equal("—", cut.Find("[data-sk='value-bitrate']").TextContent.Trim());
        Assert.Equal("—", cut.Find("[data-sk='kv-decoder']").TextContent.Trim());
        Assert.Equal("—", cut.Find("[data-sk='kv-last-segment']").TextContent.Trim());
        Assert.Equal("—", cut.Find("[data-sk='kv-segments']").TextContent.Trim());

        foreach (string tile in new[] { "bitrate", "frame", "bandwidth", "buffer" })
        {
            Assert.Equal("true", cut.Find($"[data-sk='spark-{tile}']").GetAttribute("aria-hidden"));
            Assert.Single(cut.FindAll($"[data-sk='table-{tile}']"));
            Assert.True(cut.Find($"[data-sk='table-{tile}']").HasAttribute("data-sr-only"));
        }
    }

    [Theory]
    [InlineData("Video", "video")]
    [InlineData("Network", "network")]
    [InlineData("Stream", "stream")]
    public void Each_section_is_headed_by_its_series_swatch(string section, string series)
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p =>
            p.Add(c => c.Recording, Recording("r1")));

        var heading = Assert.Single(cut.FindAll("[data-sk='stats-body'] h3"), h => h.TextContent.Trim() == section);
        Assert.Equal(series, heading.GetAttribute("data-series"));
        Assert.Equal("true", heading.QuerySelector(".swatch")?.GetAttribute("aria-hidden"));
    }

    [Fact]
    public void Video_sparklines_are_primary_and_network_sparklines_secondary()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p =>
            p.Add(c => c.Recording, Recording("r1")));

        Assert.Equal("var(--primary)", cut.Find("[data-sk='spark-bitrate']").GetAttribute("data-spark-color"));
        Assert.Equal("var(--primary)", cut.Find("[data-sk='spark-frame']").GetAttribute("data-spark-color"));
        Assert.Equal("var(--secondary)", cut.Find("[data-sk='spark-bandwidth']").GetAttribute("data-spark-color"));
        Assert.Equal("var(--secondary)", cut.Find("[data-sk='spark-buffer']").GetAttribute("data-spark-color"));
    }

    [Fact]
    public void The_stream_row_waits_hidden_for_viewer_samples()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p =>
            p.Add(c => c.Recording, Recording("r1")));

        var stream = cut.Find("[data-sk='stats-stream']");
        Assert.True(stream.HasAttribute("hidden"));
        Assert.Equal("Viewers at 0:00", stream.QuerySelector("[data-sk='stream-at']")!.TextContent.Trim());
    }

    [Fact]
    public void The_phone_tab_copies_from_a_text_button_and_has_no_stream_row()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p => p
            .Add(c => c.Recording, Recording("r1"))
            .Add(c => c.Tab, true));

        var copy = cut.Find("[data-sk='copy-stats']");
        Assert.Equal("BUTTON", copy.TagName);
        Assert.Equal("button", copy.GetAttribute("type"));
        Assert.Equal("Copy stats", copy.TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-sk='stats-stream']"));
    }

    [Fact]
    public async Task The_overlay_takes_a_recording_change_from_the_player_without_re_rendering()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p => p.Add(c => c.Recording, Recording("r1")));

        await cut.InvokeAsync(() => cut.Instance.SetRecordingAsync(Recording("r2")));

        JSRuntimeInvocation update = Assert.Single(module.Invocations, call => call.Identifier == "update");
        JsonElement payload = JsonSerializer.SerializeToElement(
            update.Arguments[1], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("r2", payload.GetProperty("recordingId").GetString());
    }

    [Fact]
    public void A_closed_card_is_hidden_and_open_state_comes_from_sk_ui()
    {
        using BunitContext closed = CreateContext(out _);
        IRenderedComponent<StatsPanel> closedCut = closed.Render<StatsPanel>(p =>
            p.Add(c => c.Recording, Recording("r1")));

        Assert.True(closedCut.Find("[data-sk-stats]").HasAttribute("hidden"));
        Assert.Equal("false", closedCut.Find("[data-sk-stats]").GetAttribute("data-open"));

        using BunitContext open = CreateContext(out _, stats: true);
        IRenderedComponent<StatsPanel> openCut = open.Render<StatsPanel>(p =>
            p.Add(c => c.Recording, Recording("r1")));

        Assert.False(openCut.Find("[data-sk-stats]").HasAttribute("hidden"));
        Assert.Equal("true", openCut.Find("[data-sk-stats]").GetAttribute("data-open"));
    }

    [Fact]
    public void The_phone_tab_form_has_no_header_and_starts_expanded()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p => p
            .Add(c => c.Recording, Recording("r1"))
            .Add(c => c.Tab, true));

        Assert.Empty(cut.FindAll("[data-stats-head], [data-sk='stats-close']"));
        Assert.Equal("tab", cut.Find("[data-sk-stats]").GetAttribute("data-chrome"));
        Assert.Equal("true", cut.Find("[data-sk-stats]").GetAttribute("data-open"));
        Assert.False(cut.Find("[data-sk-stats]").HasAttribute("hidden"));
    }

    [Fact]
    public void Initializes_the_module_with_the_recording_and_initial_open_state()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module, stats: true);

        context.Render<StatsPanel>(p => p.Add(c => c.Recording, Recording("r1")));

        JSRuntimeInvocation init = Assert.Single(module.Invocations, call => call.Identifier == "init");
        JsonElement payload = JsonSerializer.SerializeToElement(
            init.Arguments[2], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("r1", payload.GetProperty("recordingId").GetString());
        Assert.Equal("/media/ann/r1/index.m3u8", payload.GetProperty("playlistUrl").GetString());
        Assert.Equal("saved", payload.GetProperty("state").GetString());
        Assert.True(payload.GetProperty("open").GetBoolean());
    }

    [Fact]
    public async Task A_js_toggle_persists_stats_open_and_updates_the_browser()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p =>
            p.Add(c => c.Recording, Recording("r1")));

        await cut.InvokeAsync(() => cut.Instance.SetStatsOpen(true));

        UiPreferences preferences = context.Services.GetRequiredService<UiPreferences>();
        Assert.True(preferences.StatsOpen);
        Assert.Contains(module.Invocations, call => call.Identifier == "setOpen");
        Assert.Contains(
            context.JSInterop.Invocations,
            invocation => invocation.Identifier == "skPrefs.writeUi");
    }

    [Fact]
    public async Task Copy_results_are_reported_through_the_snackbar()
    {
        using BunitContext context = CreateContext(out _);
        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p =>
            p.Add(c => c.Recording, Recording("r1")));
        MudBlazor.ISnackbar snackbars = context.Services.GetRequiredService<MudBlazor.ISnackbar>();

        await cut.InvokeAsync(() => cut.Instance.Notify("Stats copied"));

        Assert.Equal("Stats copied", snackbars.CurrentMessage());

        await cut.InvokeAsync(() => cut.Instance.Notify("Could not copy stats"));

        Assert.Equal("Stats copied", snackbars.CurrentMessage());
        snackbars.Dismiss();
        Assert.Equal("Could not copy stats", snackbars.CurrentMessage());
    }

    [Fact]
    public void A_recording_change_updates_the_same_panel_without_re_rendering_it()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        RecordingView first = Recording("r1");
        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p => p.Add(c => c.Recording, first));

        cut.Render(p => p.Add(c => c.Recording, Recording("r2")));

        Assert.Single(module.Invocations, call => call.Identifier == "init");
        JSRuntimeInvocation update = Assert.Single(module.Invocations, call => call.Identifier == "update");
        JsonElement payload = JsonSerializer.SerializeToElement(
            update.Arguments[1], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("r2", payload.GetProperty("recordingId").GetString());

        // Blazor never rewrites the server-rendered playlist value the module owns.
        Assert.Equal("/media/ann/r1/index.m3u8", cut.Find("[data-sk='kv-playlist']").TextContent.Trim());
    }

    private static BunitContext CreateContext(out BunitJSModuleInterop module, bool stats = false)
    {
        var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        module = context.JSInterop.SetupModule(Module);
        module.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();

        var store = new TestClientStateStore();
        if (stats)
        {
            store.Cookies[UiPreferences.CookieName] =
                """{"v":1,"drawer":"expanded","theater":false,"stats":true,"details":true,"collapsed":[]}""";
        }

        context.Services.AddSingleton<IClientStateStore>(store);
        context.Services.AddScoped<UiPreferences>();
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddScoped<PlayerBridge>();
        return context;
    }

    private static RecordingView Recording(string id) =>
        new(id, "ann", $"Title {id}", "Food", Now.AddHours(-2), Now.AddHours(-1), 3600,
            RecordingState.Saved, 8, 1, 1024, null, $"/media/ann/{id}/index.m3u8",
            null, []);
}
