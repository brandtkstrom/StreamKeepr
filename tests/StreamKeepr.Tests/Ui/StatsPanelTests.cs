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
        Assert.Contains("sk-panel", panel.ClassList);
        Assert.Contains("sk-collapse", panel.ClassList);
        Assert.Equal("false", panel.GetAttribute("data-open"));

        var toggle = cut.Find("[data-sk='stats-toggle']");
        Assert.Equal("switch", toggle.GetAttribute("role"));
        Assert.Equal("false", toggle.GetAttribute("aria-checked"));
        Assert.Contains("Stats for nerds", toggle.TextContent);
        Assert.Empty(toggle.QuerySelectorAll("h1, h2, h3, h4, h5, h6"));
        var heading = cut.Find("section > h2.sk-visually-hidden");
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
            Assert.True(cut.Find($"[data-sk='table-{tile}']").ClassList.Contains("sk-visually-hidden"));
        }
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
        SnackbarService snackbars = context.Services.GetRequiredService<SnackbarService>();

        await cut.InvokeAsync(() => cut.Instance.Notify("Stats copied"));

        Assert.Equal("Stats copied", snackbars.Current?.Text);

        await cut.InvokeAsync(() => cut.Instance.Notify("Could not copy stats"));

        Assert.Equal("Stats copied", snackbars.Current?.Text);
        snackbars.Dismiss();
        Assert.Equal("Could not copy stats", snackbars.Current?.Text);
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
        context.Services.AddScoped<SnackbarService>();
        context.Services.AddSingleton(TimeProvider.System);
        return context;
    }

    private static RecordingView Recording(string id) =>
        new(id, "ann", $"Title {id}", "Food", Now.AddHours(-2), Now.AddHours(-1), 3600,
            RecordingState.Saved, 8, 1, 1024, null, $"/media/ann/{id}/index.m3u8",
            null, []);
}
