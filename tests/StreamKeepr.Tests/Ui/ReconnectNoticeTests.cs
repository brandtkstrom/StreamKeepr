using Bunit;
using Microsoft.AspNetCore.Components;
using StreamKeepr.Components.Layout;

namespace StreamKeepr.Tests.Ui;

public sealed class ReconnectNoticeTests
{
    [Fact]
    public void Renders_a_non_modal_status_banner_with_retry()
    {
        using var context = new BunitContext();
        var retried = false;

        var notice = context.Render<ReconnectNotice>(parameters => parameters
            .Add(component => component.Visible, true)
            .Add(component => component.OnRetry,
                EventCallback.Factory.Create(this, () => retried = true)));

        var banner = notice.Find("[data-reconnect-notice]");
        Assert.Equal("status", banner.GetAttribute("role"));
        Assert.Contains("Live updates paused", banner.TextContent);

        notice.Find("button").Click();

        Assert.True(retried);
    }

    [Fact]
    public void Does_not_render_while_the_template_modal_is_visible()
    {
        using var context = new BunitContext();

        var notice = context.Render<ReconnectNotice>(parameters => parameters
            .Add(component => component.Visible, true)
            .Add(component => component.ModalVisible, true));

        Assert.Empty(notice.FindAll("[data-reconnect-notice]"));
    }

    [Fact]
    public void Does_not_render_when_hidden()
    {
        using var context = new BunitContext();

        var notice = context.Render<ReconnectNotice>(parameters => parameters
            .Add(component => component.Visible, false));

        Assert.Empty(notice.FindAll("[data-reconnect-notice]"));
    }

    [Fact]
    public void Uses_a_custom_message()
    {
        using var context = new BunitContext();

        var notice = context.Render<ReconnectNotice>(parameters => parameters
            .Add(component => component.Visible, true)
            .Add(component => component.Message, "The backend is unavailable."));

        Assert.Contains("The backend is unavailable.", notice.Markup);
    }
}
