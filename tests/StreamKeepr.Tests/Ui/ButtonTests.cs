using Bunit;
using Microsoft.AspNetCore.Components;
using StreamKeepr.Components.Primitives;

namespace StreamKeepr.Tests.Ui;

public sealed class ButtonTests
{
    [Fact]
    public void Renders_a_filled_button_by_default()
    {
        using var context = new BunitContext();
        var cut = context.Render<Button>(parameters => parameters
            .AddChildContent("Save"));

        var button = cut.Find("button");
        Assert.Equal("button", button.GetAttribute("type"));
        Assert.Equal("filled", button.GetAttribute("data-variant"));
        Assert.Equal("Save", button.TextContent);
    }

    [Theory]
    [InlineData(ButtonVariant.Filled, "filled")]
    [InlineData(ButtonVariant.Tonal, "tonal")]
    [InlineData(ButtonVariant.Outlined, "outlined")]
    [InlineData(ButtonVariant.Text, "text")]
    [InlineData(ButtonVariant.Danger, "danger")]
    public void Exposes_the_variant(ButtonVariant variant, string expectedVariant)
    {
        using var context = new BunitContext();
        var cut = context.Render<Button>(parameters => parameters
            .Add(component => component.Variant, variant)
            .AddChildContent("Action"));

        Assert.Equal(expectedVariant, cut.Find("button").GetAttribute("data-variant"));
    }

    [Fact]
    public void Click_invokes_the_callback()
    {
        using var context = new BunitContext();
        var clicked = false;
        var cut = context.Render<Button>(parameters => parameters
            .Add(component => component.OnClick,
                EventCallback.Factory.Create(this, () => clicked = true))
            .AddChildContent("Go"));

        cut.Find("button").Click();

        Assert.True(clicked);
    }

    [Fact]
    public void Disabled_and_additional_attributes_are_forwarded()
    {
        using var context = new BunitContext();
        var cut = context.Render<Button>(parameters => parameters
            .Add(component => component.Disabled, true)
            .AddUnmatched("aria-label", "Delete recording")
            .AddUnmatched("data-testid", "delete")
            .AddChildContent("Delete"));

        var button = cut.Find("button");
        Assert.True(button.HasAttribute("disabled"));
        Assert.Equal("Delete recording", button.GetAttribute("aria-label"));
        Assert.Equal("delete", button.GetAttribute("data-testid"));
    }

    [Fact]
    public void Type_can_be_a_submit_button()
    {
        using var context = new BunitContext();
        var cut = context.Render<Button>(parameters => parameters
            .Add(component => component.Type, "submit")
            .AddChildContent("Submit"));

        Assert.Equal("submit", cut.Find("button").GetAttribute("type"));
    }
}
