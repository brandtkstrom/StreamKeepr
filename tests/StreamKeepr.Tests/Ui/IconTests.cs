using Bunit;
using Microsoft.AspNetCore.Components;
using StreamKeepr.Components.Primitives;

namespace StreamKeepr.Tests.Ui;

public sealed class IconTests
{
    [Fact]
    public void Is_decorative_by_default()
    {
        using var context = new BunitContext();
        var cut = context.Render<Icon>(parameters => parameters
            .Add(component => component.Name, "home"));

        var icon = cut.Find(".sk-icon");
        Assert.Equal("home", icon.TextContent);
        Assert.Equal("true", icon.GetAttribute("aria-hidden"));
        Assert.Null(icon.GetAttribute("role"));
        Assert.Null(icon.GetAttribute("aria-label"));
    }

    [Fact]
    public void A_label_makes_the_icon_meaningful()
    {
        using var context = new BunitContext();
        var cut = context.Render<Icon>(parameters => parameters
            .Add(component => component.Name, "fiber_manual_record")
            .Add(component => component.Label, "Recording"));

        var icon = cut.Find(".sk-icon");
        Assert.Equal("img", icon.GetAttribute("role"));
        Assert.Equal("Recording", icon.GetAttribute("aria-label"));
        Assert.Null(icon.GetAttribute("aria-hidden"));
    }

    [Theory]
    [InlineData(IconSize.Small, "xs")]
    [InlineData(IconSize.Medium, "sm")]
    [InlineData(IconSize.Large, null)]
    public void Sizes_map_to_the_design_system_classes(IconSize size, string? expectedClass)
    {
        using var context = new BunitContext();
        var cut = context.Render<Icon>(parameters => parameters
            .Add(component => component.Name, "home")
            .Add(component => component.Size, size));

        var classes = cut.Find(".sk-icon").ClassList;
        if (expectedClass is null)
            Assert.DoesNotContain("sm", classes);
        else
            Assert.Contains(expectedClass, classes);
    }

    [Fact]
    public void Fill_adds_the_material_symbols_fill_style()
    {
        using var context = new BunitContext();
        var cut = context.Render<Icon>(parameters => parameters
            .Add(component => component.Name, "fiber_manual_record")
            .Add(component => component.Fill, true));

        Assert.Contains("fill", cut.Find(".sk-icon").ClassList);
    }

    [Fact]
    public void Additional_attributes_are_forwarded()
    {
        using var context = new BunitContext();
        var cut = context.Render<Icon>(parameters => parameters
            .Add(component => component.Name, "home")
            .AddUnmatched("data-testid", "home-icon"));

        Assert.Equal("home-icon", cut.Find(".sk-icon").GetAttribute("data-testid"));
    }

    [Fact]
    public void Icon_button_uses_the_label_as_its_accessible_name()
    {
        using var context = new BunitContext();
        var cut = context.Render<IconButton>(parameters => parameters
            .Add(component => component.Icon, "settings")
            .Add(component => component.Label, "Settings"));

        var button = cut.Find("button");
        Assert.Equal("Settings", button.GetAttribute("aria-label"));
        Assert.Contains("sk-icon-btn", button.ClassList);
        Assert.Equal("settings", cut.Find(".sk-icon").TextContent);
        Assert.Null(button.GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Icon_button_exposes_active_and_toggle_state()
    {
        using var context = new BunitContext();
        var cut = context.Render<IconButton>(parameters => parameters
            .Add(component => component.Icon, "query_stats")
            .Add(component => component.Label, "Stats")
            .Add(component => component.Active, true)
            .Add(component => component.Toggle, true));

        var button = cut.Find("button");
        Assert.Contains("on", button.ClassList);
        Assert.Equal("true", button.GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Icon_button_click_and_disabled_state_are_forwarded()
    {
        using var context = new BunitContext();
        var clicked = false;
        var cut = context.Render<IconButton>(parameters => parameters
            .Add(component => component.Icon, "settings")
            .Add(component => component.Label, "Settings")
            .Add(component => component.Disabled, true)
            .Add(component => component.OnClick,
                EventCallback.Factory.Create(this, () => clicked = true)));

        Assert.True(cut.Find("button").HasAttribute("disabled"));
        Assert.False(clicked);
    }
}
