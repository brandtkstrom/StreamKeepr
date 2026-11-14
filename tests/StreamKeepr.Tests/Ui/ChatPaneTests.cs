using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Components.Features.Chat;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class ChatPaneTests
{
    [Fact]
    public void Renders_a_collapsed_accessible_pane()
    {
        using BunitContext context = HomePlayerTests.CreateContext(
            out BunitJSModuleInterop module, out TestLibraryView _);
        context.JSInterop.SetupModule("./Components/Features/Chat/ChatPane.razor.js").Mode =
            JSRuntimeMode.Loose;

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
        context.JSInterop.SetupModule("./Components/Features/Chat/ChatPane.razor.js").Mode =
            JSRuntimeMode.Loose;
        IRenderedComponent<ChatPane> cut = context.Render<ChatPane>(parameters => parameters
            .Add(component => component.Recording, WithChat())
            .Add(component => component.Open, false));

        await cut.Instance.SetChatOpen(true);

        Assert.True(context.Services.GetRequiredService<UiPreferences>().ChatOpen);
    }

    private static RecordingView WithChat() =>
        VideoPlayerTests.Recording("r1", "First") with { HasChat = true };
}
