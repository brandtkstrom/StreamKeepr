using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using StreamKeepr.Components.Features.Player;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class VideoPlayerTests
{
    private const string Module = "./Components/Features/Player/VideoPlayer.razor.js";
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    // Keep in step with HOOKS in VideoPlayer.razor.js; the Vitest fixture mirrors these.
    private static readonly string[] Hooks =
    [
        "video", "title", "streamer", "chapter", "chapter-title", "chip", "center", "spinner", "overlay", "overlay-message",
        "retry", "seek", "buffered", "played", "ticks", "knob", "hover-time", "play", "back",
        "forward", "mute", "volume", "volume-level", "current", "duration", "speed", "speed-menu",
        "stats", "chat", "edge", "live", "waiting", "pip", "theater", "fullscreen",
    ];

    [Fact]
    public void Renders_the_skeleton_the_module_expects_with_a_bare_video()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<VideoPlayer> cut = context.Render<VideoPlayer>(p =>
            p.Add(c => c.Recording, Recording("r1", "First")));

        foreach (string hook in Hooks)
            Assert.Single(cut.FindAll($"[data-sk='{hook}']"));

        var video = cut.Find("video");
        Assert.False(video.HasAttribute("controls"));
        Assert.False(video.HasAttribute("autoplay"));
        Assert.True(video.HasAttribute("playsinline"));
        Assert.Equal("metadata", video.GetAttribute("preload"));
        Assert.Equal("/thumbs/r1.jpg", video.GetAttribute("poster"));

        var seek = cut.Find("[data-sk='seek']");
        Assert.Equal("slider", seek.GetAttribute("role"));
        Assert.Equal("0", seek.GetAttribute("tabindex"));
        Assert.Equal("true", seek.GetAttribute("aria-disabled"));
        Assert.Equal("0:00 of 1:00:00", seek.GetAttribute("aria-valuetext"));

        // The title strip's chapter line starts hidden; the module names the chapter it finds.
        var chapter = cut.Find("[data-sk='chapter']");
        Assert.True(chapter.HasAttribute("hidden"));
        Assert.Equal("streamer", chapter.PreviousElementSibling?.GetAttribute("data-sk"));

        // The live control is the way back from a review position. It starts hidden, names the
        // action, and carries the short phone label as well as the full one.
        var live = cut.Find("[data-sk='live']");
        Assert.True(live.HasAttribute("hidden"));
        Assert.Equal("Jump to live edge", live.GetAttribute("aria-label"));
        Assert.Contains("Jump to live", live.TextContent);
        Assert.Contains("Live", live.TextContent);

        Assert.Equal(
            ["0.5", "1", "1.5", "2"],
            cut.FindAll("[data-sk='speed-menu'] [role='menuitemradio']").Select(i => i.GetAttribute("data-rate")));
    }

    [Theory]
    [InlineData("center", "PlayArrow")]
    [InlineData("play", "PlayArrow")]
    [InlineData("back", "Replay10")]
    [InlineData("forward", "Forward10")]
    [InlineData("mute", "VolumeUp")]
    [InlineData("stats", "QueryStats")]
    [InlineData("chat", "Chat")]
    [InlineData("pip", "PictureInPictureAlt")]
    [InlineData("theater", "WidthWide")]
    [InlineData("fullscreen", "Fullscreen")]
    public void Controls_draw_the_Material_Rounded_glyph_as_inline_svg(string control, string glyph)
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<VideoPlayer> cut = context.Render<VideoPlayer>(p =>
            p.Add(c => c.Recording, Recording("r1", "First")));

        var svg = cut.Find($"[data-sk='{control}'] > svg");
        Assert.Equal(glyph, svg.GetAttribute("data-icon"));
        Assert.Equal("true", svg.GetAttribute("aria-hidden"));
        Assert.Equal("", cut.Find($"[data-sk='{control}']").TextContent.Trim());
    }

    [Fact]
    public void The_chat_toggle_waits_for_a_chat_pane_and_names_the_C_shortcut()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<VideoPlayer> cut = context.Render<VideoPlayer>(p =>
            p.Add(c => c.Recording, Recording("r1", "First")));

        var chat = cut.Find("[data-sk='chat']");
        Assert.Equal("Chat", chat.GetAttribute("aria-label"));
        Assert.Equal("C", chat.GetAttribute("aria-keyshortcuts"));
        Assert.Equal("false", chat.GetAttribute("aria-pressed"));
        Assert.True(chat.HasAttribute("hidden"));
        // It sits beside the stats toggle.
        Assert.Equal("stats", chat.PreviousElementSibling?.GetAttribute("data-sk"));
    }

    [Fact]
    public void Every_speed_item_carries_the_check_glyph_and_the_error_overlay_its_icon()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<VideoPlayer> cut = context.Render<VideoPlayer>(p =>
            p.Add(c => c.Recording, Recording("r1", "First")));

        Assert.All(
            cut.FindAll("[data-sk='speed-menu'] [role='menuitemradio']"),
            item => Assert.Equal("Check", item.QuerySelector("svg")?.GetAttribute("data-icon")));
        Assert.Equal("Error", cut.Find("[data-sk='overlay'] > svg").GetAttribute("data-icon"));
    }

    [Fact]
    public void Initializes_the_module_with_the_camel_case_payload()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);

        context.Render<VideoPlayer>(p => p.Add(c => c.Recording, Recording("r1", "First")));

        JSRuntimeInvocation init = Assert.Single(module.Invocations, call => call.Identifier == "init");
        JsonElement payload = JsonSerializer.SerializeToElement(
            init.Arguments[2], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("r1", payload.GetProperty("recordingId").GetString());
        Assert.Equal("/media/ann/r1/index.m3u8", payload.GetProperty("playlistUrl").GetString());
        Assert.Equal("saved", payload.GetProperty("state").GetString());
        Assert.Equal(3600, payload.GetProperty("durationSec").GetDouble());
        Assert.False(payload.GetProperty("statsOpen").GetBoolean());
        Assert.False(payload.GetProperty("autoplay").GetBoolean());
        Assert.True(payload.GetProperty("theaterAvailable").GetBoolean());
        Assert.Equal(60, payload.GetProperty("chapters")[0].GetProperty("startSeconds").GetDouble());
        Assert.Equal("Main", payload.GetProperty("chapters")[0].GetProperty("title").GetString());
    }

    [Fact]
    public void A_selection_load_carries_the_one_shot_autoplay_flag()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);

        IRenderedComponent<VideoPlayer> cut = context.Render<VideoPlayer>(p => p
            .Add(c => c.Recording, Recording("r1", "First"))
            .Add(c => c.Autoplay, true));

        JSRuntimeInvocation init = Assert.Single(module.Invocations, call => call.Identifier == "init");
        JsonElement payload = JsonSerializer.SerializeToElement(
            init.Arguments[2], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(payload.GetProperty("autoplay").GetBoolean());

        // The next recording is a plain navigation: its load carries the flag off again.
        cut.Render(p => p
            .Add(c => c.Recording, Recording("r2", "Second"))
            .Add(c => c.Autoplay, false));

        JSRuntimeInvocation load = Assert.Single(module.Invocations, call => call.Identifier == "load");
        JsonElement next = JsonSerializer.SerializeToElement(
            load.Arguments[1], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.False(next.GetProperty("autoplay").GetBoolean());
    }

    [Fact]
    public void Below_md_theater_availability_reaches_the_module_without_a_reload()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        RecordingView recording = Recording("r1", "First");
        IRenderedComponent<VideoPlayer> cut = context.Render<VideoPlayer>(p =>
            p.Add(c => c.Recording, recording));

        cut.Render(p =>
        {
            p.Add(c => c.Recording, recording);
            p.Add(c => c.TheaterAvailable, false);
        });

        JSRuntimeInvocation call = Assert.Single(
            module.Invocations, invocation => invocation.Identifier == "setTheaterAvailable");
        Assert.False(Assert.IsType<bool>(call.Arguments[1]));
        Assert.DoesNotContain(module.Invocations, invocation => invocation.Identifier == "load");
    }

    [Fact]
    public void The_stats_button_reflects_the_initial_ui_preference()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<VideoPlayer> cut = context.Render<VideoPlayer>(p =>
        {
            p.Add(c => c.Recording, Recording("r1", "First"));
            p.Add(c => c.StatsOpen, true);
        });

        Assert.Equal("true", cut.Find("[data-sk='stats']").GetAttribute("aria-pressed"));
        Assert.Equal("Statistics", cut.Find("[data-sk='stats']").GetAttribute("aria-label"));
        Assert.Equal("I", cut.Find("[data-sk='stats']").GetAttribute("aria-keyshortcuts"));
        // The overlay is inside the frame that goes fullscreen, before the control bar.
        var overlay = cut.Find("[data-sk-player] > [data-sk-stats]");
        Assert.Equal("overlay", overlay.GetAttribute("data-chrome"));
        Assert.Equal("bottom-bar", overlay.NextElementSibling?.GetAttribute("data-sk"));
    }

    [Fact]
    public async Task The_player_stats_button_persists_through_ui_preferences()
    {
        using BunitContext context = CreateContext(out _);
        IRenderedComponent<VideoPlayer> cut = context.Render<VideoPlayer>(p =>
            p.Add(c => c.Recording, Recording("r1", "First")));

        await cut.InvokeAsync(() => cut.Instance.SetStatsOpen(true));

        UiPreferences preferences = context.Services.GetRequiredService<UiPreferences>();
        Assert.True(preferences.StatsOpen);
        Assert.Contains(
            context.JSInterop.Invocations,
            invocation => invocation.Identifier == "skPrefs.writeUi");
    }

    [Fact]
    public void A_new_recording_loads_a_new_view_updates_and_an_unchanged_one_does_nothing()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        RecordingView first = Recording("r1", "First");
        IRenderedComponent<VideoPlayer> cut = context.Render<VideoPlayer>(p => p.Add(c => c.Recording, first));

        cut.Render(p => p.Add(c => c.Recording, first));
        Assert.DoesNotContain(module.Invocations, call => call.Identifier is "load" or "update");

        cut.Render(p => p.Add(c => c.Recording, first with { Title = "Renamed" }));
        Assert.Single(module.Invocations, call => call.Identifier == "update");

        cut.Render(p => p.Add(c => c.Recording, Recording("r2", "Second")));
        Assert.Single(module.Invocations, call => call.Identifier == "load");

        // The markup rendered once: Blazor never overwrites what the module owns.
        Assert.Equal("First", cut.Find("[data-sk='title']").TextContent);
    }

    [Fact]
    public async Task The_player_attaches_its_seek_bridge_and_detaches_on_dispose()
    {
        using BunitContext context = CreateContext(out _);
        var bridge = context.Services.GetRequiredService<PlayerBridge>();
        IRenderedComponent<VideoPlayer> cut = context.Render<VideoPlayer>(p =>
            p.Add(c => c.Recording, Recording("r1", "First")));

        Assert.True(bridge.IsReady);

        await cut.Instance.DisposeAsync();

        Assert.False(bridge.IsReady);
    }

    [Fact]
    public async Task Disposing_during_init_never_attaches_the_bridge_or_seeks_the_module()
    {
        using BunitContext context = CreateContext(out _);
        var runtime = new BlockingJsRuntime();
        context.Services.AddSingleton<IJSRuntime>(runtime);
        var bridge = context.Services.GetRequiredService<PlayerBridge>();
        IRenderedComponent<VideoPlayer> cut = context.Render<VideoPlayer>(p =>
            p.Add(c => c.Recording, Recording("r1", "First")));

        await runtime.Module.InitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(bridge.IsReady);

        await cut.Instance.DisposeAsync();

        Assert.True(runtime.Module.Disposed);
        Assert.False(bridge.IsReady);

        // Release init on the renderer so its continuation runs after disposal.
        await cut.InvokeAsync(() => runtime.Module.ReleaseInit.TrySetResult(true));
        await cut.InvokeAsync(() => { });

        Assert.False(bridge.IsReady);
        await bridge.SeekAsync(12);
        Assert.Equal(0, runtime.Module.SeekCalls);
    }

    [Fact]
    public async Task Theater_follows_ui_preferences_both_ways()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        UiPreferences preferences = context.Services.GetRequiredService<UiPreferences>();
        IRenderedComponent<VideoPlayer> cut = context.Render<VideoPlayer>(
            p => p.Add(c => c.Recording, Recording("r1", "First")));
        Assert.Equal("false", cut.Find("[data-sk='theater']").GetAttribute("aria-pressed"));

        await cut.InvokeAsync(() => cut.Instance.SetTheater(true));
        Assert.True(preferences.Theater);
        cut.WaitForAssertion(() => Assert.Contains(module.Invocations,
            call => call.Identifier == "setTheater" && Equals(call.Arguments[1], true)));

        await cut.InvokeAsync(() => preferences.SetTheater(false));
        cut.WaitForAssertion(() => Assert.Contains(module.Invocations,
            call => call.Identifier == "setTheater" && Equals(call.Arguments[1], false)));
    }

    [Fact]
    public async Task Progress_calls_reach_the_store_for_the_recording_they_name()
    {
        using BunitContext context = CreateContext(out _);
        var store = (TestProgressStore)context.Services.GetRequiredService<IProgressStore>();
        IRenderedComponent<VideoPlayer> cut = context.Render<VideoPlayer>(
            p => p.Add(c => c.Recording, Recording("r1", "First")));

        await cut.InvokeAsync(() => cut.Instance.SaveProgress("r1", 42.5));
        Assert.Equal(42.5, store.Get("r1"));

        await cut.InvokeAsync(() => cut.Instance.ClearProgress("r1"));
        Assert.Null(store.Get("r1"));
        Assert.Equal(["r1"], store.Cleared);
    }

    [Fact]
    public async Task Dispose_releases_the_module()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        IRenderedComponent<VideoPlayer> cut = context.Render<VideoPlayer>(p =>
            p.Add(c => c.Recording, Recording("r1", "First")));

        await cut.Instance.DisposeAsync();

        Assert.Contains(module.Invocations, call => call.Identifier == "dispose");
    }

    private static BunitContext CreateContext(out BunitJSModuleInterop module)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        module = context.JSInterop.SetupModule(Module);
        module.Mode = JSRuntimeMode.Loose;
        // The Statistics overlay renders inside the player.
        context.JSInterop.SetupModule("./Components/Features/Player/StatsPanel.razor.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddStreamKeeprMudServices();
        context.Services.AddLogging();
        context.Services.AddSingleton<IClientStateStore>(new TestClientStateStore());
        context.Services.AddScoped<UiPreferences>();
        context.Services.AddScoped<PlayerBridge>();
        context.Services.AddSingleton<IProgressStore>(new TestProgressStore());
        return context;
    }

    internal static RecordingView Recording(string id, string title) =>
        new(id, "ann", title, "Food", Now.AddHours(-2), Now.AddHours(-1), 3600,
            RecordingState.Saved, 8, 1, 1024, $"/thumbs/{id}.jpg", $"/media/ann/{id}/index.m3u8",
            null, [new Chapter(60, "Main", "Food")]);

    private sealed class BlockingJsRuntime : IJSRuntime
    {
        public BlockingModule Module { get; } = new();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier == "import")
                return ValueTask.FromResult((TValue)(object)Module);

            throw new NotSupportedException($"Unexpected JS runtime call '{identifier}'.");
        }
    }

    private sealed class BlockingModule : IJSObjectReference
    {
        public TaskCompletionSource<bool> InitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseInit { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SeekCalls { get; private set; }

        public bool Disposed { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            switch (identifier)
            {
                case "init":
                    InitStarted.TrySetResult(true);
                    return new ValueTask<TValue>(WaitForReleaseAsync<TValue>());
                case "dispose":
                    return ValueTask.FromResult(default(TValue)!);
                case "seek":
                    SeekCalls++;
                    return ValueTask.FromResult(default(TValue)!);
                default:
                    throw new NotSupportedException($"Unexpected JS module call '{identifier}'.");
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        private async Task<TValue> WaitForReleaseAsync<TValue>()
        {
            await ReleaseInit.Task;
            return default!;
        }
    }
}
