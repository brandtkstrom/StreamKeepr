using Bunit;
using StreamKeepr.Components.Primitives;

namespace StreamKeepr.Tests.Ui;

public sealed class TooltipTests
{
    [Fact]
    public async Task Wrapper_describes_itself_with_the_tooltip_text()
    {
        await using var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = context.Render<Tooltip>(parameters => parameters
            .Add(component => component.Text, "Connect Twitch to use this")
            .AddChildContent("<button>Export</button>"));

        var wrapper = cut.Find("[aria-describedby]");
        var bubble = cut.Find("[role='tooltip']");

        Assert.Equal(bubble.GetAttribute("id"), wrapper.GetAttribute("aria-describedby"));
        Assert.Equal("Connect Twitch to use this", bubble.TextContent);
        Assert.Empty(bubble.QuerySelectorAll("button, a, input"));
    }

    [Fact]
    public async Task Additional_attributes_apply_to_the_wrapper()
    {
        await using var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = context.Render<Tooltip>(parameters => parameters
            .Add(component => component.Text, "Reason")
            .AddUnmatched("tabindex", "0")
            .AddUnmatched("data-requires-twitch", "true")
            .AddChildContent("<button>Export</button>"));

        var wrapper = cut.Find("[aria-describedby]");
        Assert.Equal("0", wrapper.GetAttribute("tabindex"));
        Assert.True(wrapper.HasAttribute("data-requires-twitch"));
    }

    [Fact]
    public async Task The_visible_bubble_is_a_mud_tooltip_with_the_same_text()
    {
        await using var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = context.Render<Tooltip>(parameters => parameters
            .Add(component => component.Text, "Available once the recording finishes")
            .AddChildContent("<button disabled>Delete</button>"));

        var bubble = cut.FindComponent<MudBlazor.MudTooltip>();
        Assert.Equal("Available once the recording finishes", bubble.Instance.Text);
        Assert.Equal(bubble.Instance.Text, cut.Find("[role='tooltip']").TextContent);
    }
}
