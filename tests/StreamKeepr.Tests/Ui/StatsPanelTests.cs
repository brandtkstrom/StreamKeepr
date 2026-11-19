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
        "stats-toggle", "stats-hint", "stats-body", "copy-stats",
        "value-bitrate", "spark-bitrate", "tip-bitrate", "table-bitrate", "rows-bitrate",
        "value-frame", "spark-frame", "tip-frame", "table-frame", "rows-frame",
        "value-bandwidth", "spark-bandwidth", "tip-bandwidth", "table-bandwidth", "rows-bandwidth",
        "value-buffer", "spark-buffer", "tip-buffer", "table-buffer", "rows-buffer",
        "kv-resolution", "kv-codecs", "kv-dropped", "kv-decoder",
        "kv-last-segment", "kv-segments", "kv-errors", "kv-playlist",
        "viewer-chart-empty", "viewer-tile", "viewer-at", "viewer-count",
        "viewer-now", "viewer-peak", "viewer-average",
        "viewer-chart", "viewer-svg", "tip-viewers", "table-viewers", "rows-viewers",
    ];

    [Fact]
    public void Renders_the_skeleton_once_with_the_design_system_panel()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p =>
            p.Add(c => c.Recording, Recording("r1")));

        foreach (string hook in Hooks)
            Assert.Single(cut.FindAll($"[data-sk='{hook}']"));

        var panel = cut.Find("[data-sk-stats]");
        Assert.Equal("false", panel.GetAttribute("data-open"));

        var toggle = cut.Find("[data-sk='stats-toggle']");
        Assert.Equal("switch", toggle.GetAttribute("role"));
        Assert.Equal("false", toggle.GetAttribute("aria-checked"));
        Assert.Contains("Stats for nerds", toggle.TextContent);
        Assert.Empty(toggle.QuerySelectorAll("h1, h2, h3, h4, h5, h6"));
        var heading = cut.Find("section > h2[data-sr-only]");
        Assert.Equal(heading.GetAttribute("id"), panel.GetAttribute("aria-labelledby"));

        // Copy is a body action, never part of the switch row.
        Assert.Empty(toggle.QuerySelectorAll("[data-sk='copy-stats']"));
        Assert.Single(cut.FindAll("[data-sk='stats-body'] [data-sk='copy-stats']"));

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
    [InlineData("Video", "Videocam")]
    [InlineData("Network", "NetworkCheck")]
    [InlineData("Stream", "Groups")]
    public void Each_section_is_headed_by_its_series_icon(string section, string glyph)
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p =>
            p.Add(c => c.Recording, Recording("r1")));

        var heading = Assert.Single(cut.FindAll("[data-sk='stats-body'] h3"), h => h.TextContent.Trim() == section);
        Assert.Equal(glyph, heading.QuerySelector("svg")?.GetAttribute("data-icon"));
        Assert.Equal("QueryStats", cut.Find("[data-sk='stats-toggle'] > svg").GetAttribute("data-icon"));
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
    public void The_stream_section_says_there_is_no_viewer_data_yet()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p =>
            p.Add(c => c.Recording, Recording("r1")));

        Assert.Equal(
            "No viewer data for this recording.",
            cut.Find("[data-sk='stats-stream'] [data-sk='stream-empty']").TextContent.Trim());
    }

    [Fact]
    public void The_viewer_tile_and_chart_stay_hidden_until_the_module_has_samples()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p =>
            p.Add(c => c.Recording, Recording("r1")));

        var stream = cut.Find("[data-sk='stats-stream']");
        Assert.False(stream.QuerySelector("[data-sk='viewer-chart-empty']")!.HasAttribute("hidden"));
        Assert.True(stream.QuerySelector("[data-sk='viewer-tile']")!.HasAttribute("hidden"));
        Assert.True(stream.QuerySelector("[data-sk='viewer-chart']")!.HasAttribute("hidden"));
        Assert.True(stream.QuerySelector("[data-sk='table-viewers']")!.HasAttribute("hidden"));

        var tile = cut.Find("[data-sk='viewer-tile']");
        Assert.Equal("Viewers at 0:00", tile.QuerySelector("[data-sk='viewer-at']")!.TextContent.Trim());
        Assert.Equal("—", tile.QuerySelector("[data-sk='viewer-count']")!.TextContent.Trim());
        string[] rows = tile.QuerySelectorAll("dt").Select(dt => dt.TextContent.Trim()).ToArray();
        Assert.Equal(["Now", "Peak", "Average"], rows);
        foreach (string hook in new[] { "viewer-now", "viewer-peak", "viewer-average" })
            Assert.Equal("—", tile.QuerySelector($"dd[data-sk='{hook}']")!.TextContent.Trim());
    }

    [Fact]
    public void The_viewer_chart_is_a_labelled_image_with_a_screen_reader_table_beside_it()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p =>
            p.Add(c => c.Recording, Recording("r1")));

        var chart = cut.Find("[data-sk='viewer-chart']");
        Assert.Equal("img", chart.GetAttribute("role"));
        Assert.False(string.IsNullOrWhiteSpace(chart.GetAttribute("aria-label")));
        Assert.Equal("true", chart.QuerySelector("[data-sk='viewer-svg']")!.GetAttribute("aria-hidden"));
        Assert.Equal("true", chart.QuerySelector("[data-sk='tip-viewers']")!.GetAttribute("aria-hidden"));

        // An img's children are presentational, so the sample table must sit outside it.
        var table = cut.Find("[data-sk='table-viewers']");
        Assert.Null(chart.QuerySelector("table"));
        Assert.True(table.HasAttribute("data-sr-only"));
        Assert.Equal("Viewer count samples", table.QuerySelector("caption")!.TextContent.Trim());
        Assert.Equal(
            ["Time", "Viewers"],
            table.QuerySelectorAll("thead th").Select(th => th.TextContent.Trim()).ToArray());
        Assert.Empty(cut.FindAll("[data-sk='rows-viewers'] tr"));
    }

    [Fact]
    public async Task A_chart_seek_moves_the_player_through_the_bridge()
    {
        using BunitContext context = CreateContext(out _);
        var bridge = context.Services.GetRequiredService<PlayerBridge>();
        var seeks = new List<double>();
        bridge.Attach(seconds =>
        {
            seeks.Add(seconds);
            return ValueTask.CompletedTask;
        });
        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p =>
            p.Add(c => c.Recording, Recording("r1")));

        await cut.InvokeAsync(() => cut.Instance.Seek(2280.5));

        Assert.Equal([2280.5], seeks);
    }

    [Fact]
    public void Copy_stats_is_a_plain_button_the_module_owns()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p =>
            p.Add(c => c.Recording, Recording("r1")));

        var copy = cut.Find("[data-sk='copy-stats']");
        Assert.Equal("BUTTON", copy.TagName);
        Assert.Equal("button", copy.GetAttribute("type"));
        Assert.Equal("Copy stats", copy.TextContent.Trim());
    }

    [Fact]
    public void Collapsed_body_is_inert_and_open_state_comes_from_sk_ui()
    {
        using BunitContext closed = CreateContext(out _);
        IRenderedComponent<StatsPanel> closedCut = closed.Render<StatsPanel>(p =>
            p.Add(c => c.Recording, Recording("r1")));

        Assert.True(closedCut.Find("[data-sk='stats-body']").HasAttribute("inert"));
        Assert.Equal("false", closedCut.Find("[data-sk-stats]").GetAttribute("data-open"));
        Assert.Equal("false", closedCut.Find("[data-sk='stats-toggle']").GetAttribute("aria-checked"));

        using BunitContext open = CreateContext(out _, stats: true);
        IRenderedComponent<StatsPanel> openCut = open.Render<StatsPanel>(p =>
            p.Add(c => c.Recording, Recording("r1")));

        Assert.False(openCut.Find("[data-sk='stats-body']").HasAttribute("inert"));
        Assert.Equal("true", openCut.Find("[data-sk-stats]").GetAttribute("data-open"));
        Assert.Equal("true", openCut.Find("[data-sk='stats-toggle']").GetAttribute("aria-checked"));
    }

    [Fact]
    public void The_phone_tab_form_has_no_header_and_starts_expanded()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<StatsPanel> cut = context.Render<StatsPanel>(p => p
            .Add(c => c.Recording, Recording("r1"))
            .Add(c => c.Tab, true));

        Assert.Empty(cut.FindAll("[data-sk='stats-toggle']"));
        Assert.Equal("tab", cut.Find("[data-sk-stats]").GetAttribute("data-chrome"));
        Assert.Equal("true", cut.Find("[data-sk-stats]").GetAttribute("data-open"));
        Assert.False(cut.Find("[data-sk='stats-body']").HasAttribute("inert"));
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
