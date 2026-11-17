using System.Text.Json;
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
