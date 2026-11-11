using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
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
        "video", "title", "streamer", "chip", "center", "spinner", "overlay", "overlay-message",
        "retry", "seek", "buffered", "played", "ticks", "knob", "hover-time", "play", "back",
        "forward", "mute", "volume", "volume-level", "current", "duration", "speed", "speed-menu",
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

        Assert.Equal(
            ["0.5", "1", "1.5", "2"],
            cut.FindAll("[data-sk='speed-menu'] [role='menuitemradio']").Select(i => i.GetAttribute("data-rate")));
    }

    [Fact]
    public void Initializes_the_module_with_the_camel_case_payload()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);

        context.Render<VideoPlayer>(p => p.Add(c => c.Recording, Recording("r1", "First")));

        JSRuntimeInvocation init = Assert.Single(module.Invocations, call => call.Identifier == "init");
        JsonElement payload = JsonSerializer.SerializeToElement(
            init.Arguments[1], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("r1", payload.GetProperty("recordingId").GetString());
        Assert.Equal("/media/ann/r1/index.m3u8", payload.GetProperty("playlistUrl").GetString());
        Assert.Equal("saved", payload.GetProperty("state").GetString());
        Assert.Equal(3600, payload.GetProperty("durationSec").GetDouble());
        Assert.Equal(60, payload.GetProperty("chapters")[0].GetProperty("startSeconds").GetDouble());
        Assert.Equal("Main", payload.GetProperty("chapters")[0].GetProperty("title").GetString());
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
        context.Services.AddLogging();
        return context;
    }

    internal static RecordingView Recording(string id, string title) =>
        new(id, "ann", title, "Food", Now.AddHours(-2), Now.AddHours(-1), 3600,
            RecordingState.Saved, 8, 1, 1024, $"/thumbs/{id}.jpg", $"/media/ann/{id}/index.m3u8",
            null, [new Chapter(60, "Main", "Food")]);
}
