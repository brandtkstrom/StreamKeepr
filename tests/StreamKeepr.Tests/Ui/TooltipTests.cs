using Bunit;
using StreamKeepr.Components.Primitives;

namespace StreamKeepr.Tests.Ui;

public sealed class TooltipTests
{
    [Fact]
    public void Wrapper_describes_itself_with_the_tooltip_text()
    {
        using var context = new BunitContext();
        var cut = context.Render<Tooltip>(parameters => parameters
            .Add(component => component.Text, "Connect Twitch to use this")
            .AddChildContent("<button>Export</button>"));

        var wrapper = cut.Find(".sk-tooltip-wrap");
        var bubble = cut.Find("[role='tooltip']");

        Assert.Equal(bubble.GetAttribute("id"), wrapper.GetAttribute("aria-describedby"));
        Assert.Equal("Connect Twitch to use this", bubble.TextContent);
        Assert.Empty(bubble.QuerySelectorAll("button, a, input"));
    }

    [Fact]
    public void Additional_attributes_apply_to_the_wrapper()
    {
        using var context = new BunitContext();
        var cut = context.Render<Tooltip>(parameters => parameters
            .Add(component => component.Text, "Reason")
            .AddUnmatched("tabindex", "0")
            .AddUnmatched("data-requires-twitch", "true")
            .AddChildContent("<button>Export</button>"));

        var wrapper = cut.Find(".sk-tooltip-wrap");
        Assert.Equal("0", wrapper.GetAttribute("tabindex"));
        Assert.True(wrapper.HasAttribute("data-requires-twitch"));
    }
}
