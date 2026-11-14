using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StreamKeepr.Components.Pages;
using StreamKeepr.Configuration;
using StreamKeepr.Components.Features.Chat;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class ChatPaneTests
{
    private const string Module = "./Components/Features/Chat/ChatPane.razor.js";

    [Fact]
    public void Renders_a_collapsed_accessible_pane()
    {
        using BunitContext context = HomePlayerTests.CreateContext(
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
        using BunitContext context = HomePlayerTests.CreateContext(
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
    public void Home_shows_the_pane_for_captured_chat_or_for_live_capture_only_when_enabled(
        bool live, bool enabled, bool hasChat, bool expected)
    {
        using BunitContext context = HomePlayerTests.CreateContext(
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
        using BunitContext context = HomePlayerTests.CreateContext(
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
