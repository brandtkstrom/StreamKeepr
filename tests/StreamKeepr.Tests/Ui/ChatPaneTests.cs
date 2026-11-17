using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StreamKeepr.Components.Pages;
using StreamKeepr.Configuration;
using StreamKeepr.Components.Features.Chat;
using StreamKeepr.Infrastructure.Emotes;
using StreamKeepr.Tests.Emotes;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class ChatPaneTests
{
    private const string Module = "./Components/Features/Chat/ChatPane.razor.js";

    [Fact]
    public async Task Renders_a_collapsed_accessible_pane()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(
            out BunitJSModuleInterop module, out TestLibraryView _);
        context.JSInterop.SetupModule(Module).Mode = JSRuntimeMode.Loose;

        IRenderedComponent<ChatPane> cut = context.Render<ChatPane>(parameters => parameters
            .Add(component => component.Recording, WithChat())
            .Add(component => component.Open, false));

        Assert.Equal("false", cut.Find("[data-sk-chat]").GetAttribute("data-open"));
        Assert.Equal("false", cut.Find("[data-sk='chat-toggle']").GetAttribute("aria-expanded"));
        Assert.Equal("log", cut.Find("[data-sk='chat-log']").GetAttribute("role"));
        Assert.Equal("off", cut.Find("[data-sk='chat-log']").GetAttribute("aria-live"));
        Assert.True(cut.Find("[data-sk='chat-body']").HasAttribute("inert"));
    }

    [Fact]
    public async Task Persists_a_toggle_through_the_scoped_preferences()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(
            out BunitJSModuleInterop module, out TestLibraryView _);
        context.JSInterop.SetupModule(Module).Mode = JSRuntimeMode.Loose;
        IRenderedComponent<ChatPane> cut = context.Render<ChatPane>(parameters => parameters
            .Add(component => component.Recording, WithChat())
            .Add(component => component.Open, false));

        await cut.Instance.SetChatOpen(true);

        Assert.True(context.Services.GetRequiredService<UiPreferences>().ChatOpen);
    }

    [Theory]
    [InlineData(true, false, false, false)] // Live, capture off: no pane (merged dark).
    [InlineData(true, true, false, true)] // Live, capture on: waiting for chat.
    [InlineData(false, false, true, true)] // Finished with captured chat: replay.
    [InlineData(false, true, false, false)] // Finished without chat: nothing to show.
    public async Task Home_shows_the_pane_for_captured_chat_or_for_live_capture_only_when_enabled(
        bool live, bool enabled, bool hasChat, bool expected)
    {
        await using BunitContext context = HomePlayerTests.CreateContext(
            out BunitJSModuleInterop _, out TestLibraryView view);
        context.JSInterop.SetupModule(Module).Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton(Options.Create(new ChatOptions { Enabled = enabled }));
        view.FindById["r1"] = VideoPlayerTests.Recording("r1", "First") with
        {
            State = live ? RecordingState.Recording : RecordingState.Saved,
            HasChat = hasChat,
        };

        IRenderedComponent<Home> cut = context.Render<Home>(p => p.Add(c => c.RecordingId, "r1"));

        Assert.Equal(expected, cut.FindAll("[data-sk-chat]").Count == 1);
    }

    [Fact]
    public async Task A_live_library_refresh_does_not_reload_the_same_recording()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(
            out BunitJSModuleInterop module, out TestLibraryView _);
        module = context.JSInterop.SetupModule(Module);
        module.Mode = JSRuntimeMode.Loose;

        IRenderedComponent<ChatPane> cut = context.Render<ChatPane>(parameters => parameters
            .Add(component => component.Recording, LiveRecording(1))
            .Add(component => component.Open, true));
        Assert.Single(module.Invocations, call => call.Identifier == "init");
        Assert.DoesNotContain(module.Invocations, call => call.Identifier == "load");

        // The scoped LibraryView rebuilds a new instance every 500 ms while live because
        // DurationSec changes; the pane must not reapply the summary's stale HasChat.
        await cut.InvokeAsync(() => cut.Render(parameters => parameters
            .Add(component => component.Recording, LiveRecording(2))
            .Add(component => component.Open, true)));
        Assert.DoesNotContain(module.Invocations, call => call.Identifier == "load");

        // A real meaningful change still reloads the module.
        await cut.InvokeAsync(() => cut.Render(parameters => parameters
            .Add(component => component.Recording, LiveRecording(3, hasChat: true))
            .Add(component => component.Open, true)));
        Assert.Single(module.Invocations, call => call.Identifier == "load");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initializes_the_module_with_the_any_provider_flag(bool enabled)
    {
        await using BunitContext context = HomePlayerTests.CreateContext(
            out BunitJSModuleInterop module, out TestLibraryView _);
        module = context.JSInterop.SetupModule(Module);
        module.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IEmoteProvider>(
            new StubEmoteProvider { Enabled = enabled });

        context.Render<ChatPane>(p => p
            .Add(c => c.Recording, WithChat())
            .Add(c => c.Open, true));

        JSRuntimeInvocation init = Assert.Single(module.Invocations, call => call.Identifier == "init");
        JsonElement payload = JsonSerializer.SerializeToElement(
            init.Arguments[2], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(enabled, payload.GetProperty("emotesEnabled").GetBoolean());
    }

    [Fact]
    public async Task Tells_the_module_the_channel_and_its_channel_colour()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(
            out BunitJSModuleInterop module, out TestLibraryView _);
        module = context.JSInterop.SetupModule(Module);
        module.Mode = JSRuntimeMode.Loose;

        context.Render<ChatPane>(p => p
            .Add(c => c.Recording, WithChat() with { StreamerLogin = "mock_nova" })
            .Add(c => c.Open, true));

        JSRuntimeInvocation init = Assert.Single(module.Invocations, call => call.Identifier == "init");
        JsonElement payload = JsonSerializer.SerializeToElement(
            init.Arguments[2], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("mock_nova", payload.GetProperty("channelLogin").GetString());
        // The drawer's colour for mock_nova (ChannelColorTests): chat and drawer agree.
        Assert.Equal(3, payload.GetProperty("channelColor").GetInt32());
    }

    [Fact]
    public async Task The_header_toggle_draws_the_chat_glyph()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(
            out BunitJSModuleInterop module, out TestLibraryView _);
        context.JSInterop.SetupModule(Module).Mode = JSRuntimeMode.Loose;

        IRenderedComponent<ChatPane> cut = context.Render<ChatPane>(p => p
            .Add(c => c.Recording, WithChat())
            .Add(c => c.Open, true));

        Assert.Equal("Chat", cut.Find("[data-sk='chat-toggle'] > svg").GetAttribute("data-icon"));
    }

    [Fact]
    public async Task The_sync_footer_is_a_labelled_slider_over_the_endpoint_limit()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(
            out BunitJSModuleInterop module, out TestLibraryView _);
        context.JSInterop.SetupModule(Module).Mode = JSRuntimeMode.Loose;

        IRenderedComponent<ChatPane> cut = context.Render<ChatPane>(p => p
            .Add(c => c.Recording, WithChat())
            .Add(c => c.Open, true));

        IElement range = cut.Find("[data-sk='chat-sync-range']");
        Assert.Equal("range", range.GetAttribute("type"));
        Assert.Equal("-30", range.GetAttribute("min"));
        Assert.Equal("30", range.GetAttribute("max"));
        Assert.Equal("0.5", range.GetAttribute("step"));
        Assert.Equal("0", range.GetAttribute("value"));
        Assert.Equal("0 seconds", range.GetAttribute("aria-valuetext"));
        // The visible "Chat sync" label names the slider.
        IElement label = cut.Find($"label[for='{range.Id}']");
        Assert.Equal("Chat sync", label.TextContent.Trim());

        IElement reset = cut.Find("[data-sk='chat-sync-reset']");
        Assert.Equal("button", reset.GetAttribute("type"));
        Assert.Equal("Reset chat sync to 0", reset.GetAttribute("aria-label"));
        Assert.Equal("RestartAlt", reset.QuerySelector("svg")!.GetAttribute("data-icon"));
        Assert.Equal("0.0 s", cut.Find("[data-sk='chat-sync-value']").TextContent.Trim());

        string sync = cut.Find("[data-sk='chat-sync']").TextContent;
        Assert.Contains("Earlier", sync, StringComparison.Ordinal);
        Assert.Contains("Later", sync, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("[data-sk='chat-sync'] svg[data-icon='Tune']"));
        Assert.Single(cut.FindAll("[data-sk='chat-sync'] button"));
    }

    [Fact]
    public async Task The_phone_tab_form_has_no_header_and_starts_open()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(
            out BunitJSModuleInterop module, out TestLibraryView _);
        module = context.JSInterop.SetupModule(Module);
        module.Mode = JSRuntimeMode.Loose;

        IRenderedComponent<ChatPane> cut = context.Render<ChatPane>(p => p
            .Add(c => c.Recording, WithChat())
            .Add(c => c.Open, false)
            .Add(c => c.HideHeader, true));

        Assert.Empty(cut.FindAll("[data-sk='chat-toggle']"));
        Assert.Equal("true", cut.Find("[data-sk-chat]").GetAttribute("data-open"));
        Assert.False(cut.Find("[data-sk='chat-body']").HasAttribute("inert"));

        JSRuntimeInvocation init = Assert.Single(module.Invocations, call => call.Identifier == "init");
        JsonElement payload = JsonSerializer.SerializeToElement(
            init.Arguments[2], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(payload.GetProperty("open").GetBoolean());
    }

    private static RecordingView WithChat() =>
        VideoPlayerTests.Recording("r1", "First") with { HasChat = true };

    private static RecordingView LiveRecording(int durationSec, bool hasChat = false) =>
        VideoPlayerTests.Recording("r1", "First") with
        {
            State = RecordingState.Recording,
            DurationSec = durationSec,
            HasChat = hasChat,
        };
}
