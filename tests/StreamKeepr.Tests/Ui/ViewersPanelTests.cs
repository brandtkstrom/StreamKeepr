using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Components.Features.Player;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

/// <summary>The Viewers panel (design system: Viewers): the viewer chart, out of the old stats panel.</summary>
public sealed class ViewersPanelTests
{
    private const string Module = "./Components/Features/Player/ViewerChart.razor.js";
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    // Keep in step with HOOKS in ViewerChart.razor.js; the Vitest fixture mirrors these.
    private static readonly string[] Hooks =
    [
        "viewer-readout", "viewer-at", "viewer-count", "viewer-now-label", "viewer-now",
        "viewer-peak", "viewer-peak-at", "viewer-average",
        "viewer-chart", "viewer-svg", "tip-viewers", "table-viewers", "rows-viewers",
    ];

    [Fact]
    public void Renders_the_chart_skeleton_once_inside_a_disclosure_headed_viewers()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<ViewersPanel> cut = Render(context);

        foreach (string hook in Hooks)
            Assert.Single(cut.FindAll($"[data-sk='{hook}']"));
        var toggle = cut.Find("button[data-collapsible-toggle]");
        Assert.Equal("Viewers", toggle.QuerySelector("h2")!.TextContent.Trim());
        Assert.Equal("true", toggle.GetAttribute("aria-expanded"));
        Assert.NotNull(toggle.QuerySelector("[data-viewers-hint]"));
    }

    [Fact]
    public async Task The_panel_stays_hidden_until_the_chart_finds_samples()
    {
        using BunitContext context = CreateContext(out _);
        IRenderedComponent<ViewersPanel> cut = Render(context);
        Assert.True(cut.Find("[data-viewers-panel]").HasAttribute("hidden"));

        var chart = cut.FindComponent<ViewerChart>();
        await cut.InvokeAsync(() => chart.Instance.SetHasSamples(true));
        Assert.False(cut.Find("[data-viewers-panel]").HasAttribute("hidden"));

        await cut.InvokeAsync(() => chart.Instance.SetHasSamples(false));
        Assert.True(cut.Find("[data-viewers-panel]").HasAttribute("hidden"));
    }

    [Fact]
    public async Task The_header_toggles_and_remembers_the_open_state()
    {
        using BunitContext context = CreateContext(out _);
        IRenderedComponent<ViewersPanel> cut = Render(context);

        await cut.Find("button[data-collapsible-toggle]").ClickAsync(new());

        Assert.False(context.Services.GetRequiredService<UiPreferences>().ViewersOpen);
        Assert.Equal("false", cut.Find("button[data-collapsible-toggle]").GetAttribute("aria-expanded"));
        Assert.Contains(context.JSInterop.Invocations, call => call.Identifier == "skPrefs.writeUi");
    }

    [Fact]
    public void The_phone_tab_form_has_no_header()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<ViewersPanel> cut = context.Render<ViewersPanel>(p => p
            .Add(c => c.Recording, Recording("r1"))
            .Add(c => c.Tab, true));

        Assert.Empty(cut.FindAll("button, h2"));
        Assert.Single(cut.FindAll("[data-sk='viewer-chart']"));
        Assert.True(cut.Find("[data-viewers-panel]").HasAttribute("hidden"));
    }

    [Fact]
    public void The_chart_is_a_labelled_image_with_a_screen_reader_table_beside_it()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<ViewersPanel> cut = Render(context);

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

        var readout = cut.Find("[data-sk='viewer-readout']");
        Assert.Equal(["Now", "Average", "Peak"], readout.QuerySelectorAll("dt").Select(dt => dt.TextContent.Trim()).ToArray());
        Assert.Equal("Now", cut.Find("[data-sk='viewer-now-label']").TextContent.Trim());
        Assert.Equal("—", cut.Find("[data-sk='viewer-peak']").TextContent.Trim());
        Assert.Equal("", cut.Find("[data-sk='viewer-peak-at']").TextContent.Trim());
    }

    [Fact]
    public async Task A_chart_seek_moves_the_player_through_the_bridge()
    {
        using BunitContext context = CreateContext(out _);
        var seeks = new List<double>();
        context.Services.GetRequiredService<PlayerBridge>().Attach(seconds =>
        {
            seeks.Add(seconds);
            return ValueTask.CompletedTask;
        });
        IRenderedComponent<ViewersPanel> cut = Render(context);

        await cut.InvokeAsync(() => cut.FindComponent<ViewerChart>().Instance.Seek(2280.5));

        Assert.Equal([2280.5], seeks);
    }

    [Fact]
    public void Initializes_the_module_and_passes_a_recording_change_without_re_rendering()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        IRenderedComponent<ViewersPanel> cut = Render(context);

        JSRuntimeInvocation init = Assert.Single(module.Invocations, call => call.Identifier == "init");
        Assert.Equal("r1", Payload(init.Arguments[2]).GetProperty("recordingId").GetString());
        Assert.Equal("saved", Payload(init.Arguments[2]).GetProperty("state").GetString());
        Assert.Empty(Payload(init.Arguments[2]).GetProperty("chapters").EnumerateArray());

        cut.Render(p => p.Add(c => c.Recording, Recording("r2")));

        Assert.Single(module.Invocations, call => call.Identifier == "init");
        JSRuntimeInvocation update = Assert.Single(module.Invocations, call => call.Identifier == "update");
        Assert.Equal("r2", Payload(update.Arguments[1]).GetProperty("recordingId").GetString());
    }

    [Fact]
    public void Sends_the_chapter_start_seconds_to_the_chart()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        RecordingView recording = Recording("r1") with
        {
            Chapters =
            [
                new Chapter(750, "Route planning", "Celeste"),
                new Chapter(2280, "Full runs", "Celeste"),
            ],
        };

        context.Render<ViewersPanel>(p => p.Add(c => c.Recording, recording));

        JSRuntimeInvocation init = Assert.Single(module.Invocations, call => call.Identifier == "init");
        Assert.Equal(
            [750d, 2280d],
            Payload(init.Arguments[2]).GetProperty("chapters").EnumerateArray()
                .Select(value => value.GetDouble()).ToArray());
    }

    private static JsonElement Payload(object? argument) =>
        JsonSerializer.SerializeToElement(argument, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static IRenderedComponent<ViewersPanel> Render(BunitContext context) =>
        context.Render<ViewersPanel>(p => p.Add(c => c.Recording, Recording("r1")));

    private static BunitContext CreateContext(out BunitJSModuleInterop module)
    {
        var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        module = context.JSInterop.SetupModule(Module);
        module.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();
        context.Services.AddSingleton<IClientStateStore>(new TestClientStateStore());
        context.Services.AddScoped<UiPreferences>();
        context.Services.AddScoped<PlayerBridge>();
        return context;
    }

    private static RecordingView Recording(string id) =>
        new(id, "ann", $"Title {id}", "Food", Now.AddHours(-2), Now.AddHours(-1), 3600,
            RecordingState.Saved, 8, 1, 1024, null, $"/media/ann/{id}/index.m3u8",
            null, []);
}
