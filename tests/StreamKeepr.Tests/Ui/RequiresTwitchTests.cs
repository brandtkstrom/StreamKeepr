using Bunit;
using StreamKeepr.Components.Features.Auth;

namespace StreamKeepr.Tests.Ui;

public sealed class RequiresTwitchTests
{
    [Fact]
    public void Signed_out_keeps_the_child_visible_but_inert_with_a_tooltip()
    {
        using var context = CreateContext(signedIn: false);
        var cut = RenderRequiresTwitch(context);

        Assert.NotNull(cut.Find("#export"));
        Assert.Equal("Export MP4", cut.Find("#export").TextContent.Trim());

        var wrapper = cut.Find("[data-requires-twitch]");
        var inert = cut.Find(".sk-requires-twitch");
        var tooltip = cut.Find("[role='tooltip']");

        Assert.True(inert.HasAttribute("inert"));
        Assert.Equal("0", wrapper.GetAttribute("tabindex"));
        Assert.Equal(tooltip.GetAttribute("id"), wrapper.GetAttribute("aria-describedby"));
        Assert.Equal("Connect Twitch to use this", tooltip.TextContent);
    }

    [Fact]
    public void Signed_in_renders_the_child_without_the_inert_wrapper()
    {
        using var context = CreateContext(signedIn: true);
        var cut = RenderRequiresTwitch(context);

        Assert.NotNull(cut.Find("#export"));
        Assert.Empty(cut.FindAll(".sk-requires-twitch"));
        Assert.Empty(cut.FindAll("[data-requires-twitch]"));
        Assert.Empty(cut.FindAll("[role='tooltip']"));
    }

    [Fact]
    public void The_tooltip_message_can_be_overridden()
    {
        using var context = CreateContext(signedIn: false);
        var cut = context.Render<RequiresTwitch>(parameters => parameters
            .Add(component => component.Message, "Sign in to export")
            .AddChildContent("<button>Export</button>"));

        Assert.Equal("Sign in to export", cut.Find("[role='tooltip']").TextContent);
    }

    private static BunitContext CreateContext(bool signedIn)
    {
        var context = new BunitContext();
        var authorization = context.AddAuthorization();

        if (signedIn)
            authorization.SetAuthorized("pixelwren");
        else
            authorization.SetNotAuthorized();

        return context;
    }

    private static IRenderedComponent<RequiresTwitch> RenderRequiresTwitch(BunitContext context) =>
        context.Render<RequiresTwitch>(parameters => parameters
            .AddChildContent("<button id='export'>Export MP4</button>"));
}
