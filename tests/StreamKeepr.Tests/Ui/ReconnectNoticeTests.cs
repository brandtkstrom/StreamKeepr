using Bunit;
using StreamKeepr.Components.Layout;

namespace StreamKeepr.Tests.Ui;

public sealed class ReconnectNoticeTests
{
    [Fact]
    public async Task Renders_a_non_modal_status_with_a_retry_that_works_without_the_circuit()
    {
        await using var context = CreateContext();

        var notice = context.Render<ReconnectNotice>(parameters => parameters
            .Add(component => component.Visible, true));

        var banner = notice.Find("[data-reconnect-notice]");
        Assert.Equal("status", banner.GetAttribute("role"));
        Assert.Contains("Live updates paused", banner.TextContent);

        // reconnect.js serves the button by its hook: a Blazor handler would not fire while
        // the circuit is down.
        var retry = banner.QuerySelector("button[data-reconnect-retry]")!;
        Assert.Equal("Retry", retry.TextContent.Trim());
    }

    [Fact]
    public async Task Does_not_render_while_the_template_modal_is_visible()
    {
        await using var context = CreateContext();

        var notice = context.Render<ReconnectNotice>(parameters => parameters
            .Add(component => component.Visible, true)
            .Add(component => component.ModalVisible, true));

        Assert.Empty(notice.FindAll("[data-reconnect-notice]"));
    }

    [Fact]
    public async Task Does_not_render_when_hidden()
    {
        await using var context = CreateContext();

        var notice = context.Render<ReconnectNotice>(parameters => parameters
            .Add(component => component.Visible, false));

        Assert.Empty(notice.FindAll("[data-reconnect-notice]"));
    }

    [Fact]
    public async Task Uses_a_custom_message()
    {
        await using var context = CreateContext();

        var notice = context.Render<ReconnectNotice>(parameters => parameters
            .Add(component => component.Visible, true)
            .Add(component => component.Message, "The backend is unavailable."));

        Assert.Contains("The backend is unavailable.", notice.Find("[data-reconnect-notice]").TextContent);
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }
}
