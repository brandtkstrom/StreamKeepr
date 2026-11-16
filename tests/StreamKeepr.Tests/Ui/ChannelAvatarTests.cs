using Bunit;
using MudBlazor.Services;
using StreamKeepr.Components.Features.Library;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class ChannelAvatarTests
{
    [Fact]
    public async Task Without_an_image_it_shows_the_initial_on_the_logins_channel_colour()
    {
        await using var context = CreateContext();
        var cut = context.Render<ChannelAvatar>(parameters => parameters
            .Add(component => component.Login, "pixelwren")
            .Add(component => component.Name, "Pixel Wren"));

        var avatar = cut.Find("[role='img']");
        Assert.Equal("Pixel Wren", avatar.GetAttribute("aria-label"));
        Assert.Equal("P", avatar.TextContent.Trim());
        Assert.Equal(ChannelColor.For("pixelwren").ToString(), avatar.GetAttribute("data-channel-color"));
        Assert.Empty(cut.FindAll("[data-live-ring]"));
    }

    [Fact]
    public async Task The_colour_follows_the_login_not_the_display_name()
    {
        await using var context = CreateContext();
        var renamed = context.Render<ChannelAvatar>(parameters => parameters
            .Add(component => component.Login, "pixelwren")
            .Add(component => component.Name, "Someone Else Entirely"));

        Assert.Equal(
            ChannelColor.For("pixelwren").ToString(),
            renamed.Find("[role='img']").GetAttribute("data-channel-color"));
    }

    [Fact]
    public async Task An_image_replaces_the_initial_and_a_live_channel_carries_the_badge()
    {
        await using var context = CreateContext();
        var cut = context.Render<ChannelAvatar>(parameters => parameters
            .Add(component => component.Login, "pixelwren")
            .Add(component => component.Name, "Pixel Wren")
            .Add(component => component.ImageUrl, "/img/pixelwren.png")
            .Add(component => component.Live, true));

        var avatar = cut.Find("[role='img']");
        var image = avatar.QuerySelector("img")!;
        Assert.Equal("/img/pixelwren.png", image.GetAttribute("src"));
        Assert.Equal("", image.GetAttribute("alt"));
        Assert.Equal("", avatar.TextContent.Trim());
        Assert.NotNull(cut.Find("[data-live-ring]"));
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        return context;
    }
}
