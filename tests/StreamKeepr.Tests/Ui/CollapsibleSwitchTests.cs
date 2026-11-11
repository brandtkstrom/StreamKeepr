using Bunit;
using Microsoft.AspNetCore.Components;
using StreamKeepr.Components.Primitives;

namespace StreamKeepr.Tests.Ui;

public sealed class CollapsibleSwitchTests
{
    [Fact]
    public void Collapsible_links_the_header_button_to_an_inert_body_when_closed()
    {
        using var context = new BunitContext();
        var cut = context.Render<Collapsible>(parameters => parameters
            .Add(component => component.Open, false)
            .Add(component => component.Header, "<h3>Recording details</h3>")
            .AddChildContent("<p>Body</p>"));

        var section = cut.Find("section.sk-collapse");
        var header = cut.Find("button.sk-collapse-head");
        var body = cut.Find(".sk-collapse-body");

        Assert.Equal("false", section.GetAttribute("aria-expanded"));
        Assert.Equal("false", header.GetAttribute("aria-expanded"));
        Assert.Equal(header.GetAttribute("aria-controls"), body.GetAttribute("id"));
        Assert.True(body.HasAttribute("inert"));
        Assert.Contains("Recording details", header.TextContent);
    }

    [Fact]
    public void Collapsible_removes_inert_when_open()
    {
        using var context = new BunitContext();
        var cut = context.Render<Collapsible>(parameters => parameters
            .Add(component => component.Open, true)
            .Add(component => component.Header, "<h3>Recording details</h3>")
            .AddChildContent("<p>Body</p>"));

        Assert.Equal("true", cut.Find("section.sk-collapse").GetAttribute("aria-expanded"));
        Assert.False(cut.Find(".sk-collapse-body").HasAttribute("inert"));
    }

    [Fact]
    public void Collapsible_header_reports_the_requested_state()
    {
        using var context = new BunitContext();
        var toggle = false;
        var cut = context.Render<Collapsible>(parameters => parameters
            .Add(component => component.Open, false)
            .Add(component => component.Header, "<h3>Details</h3>")
            .Add(component => component.OnToggle,
                EventCallback.Factory.Create<bool>(this, value => toggle = value))
            .AddChildContent("<p>Body</p>"));

        cut.Find("button.sk-collapse-head").Click();

        Assert.True(toggle);
    }

    [Fact]
    public void Switch_exposes_role_checked_state_and_its_label()
    {
        using var context = new BunitContext();
        var cut = context.Render<Switch>(parameters => parameters
            .Add(component => component.Checked, false)
            .Add(component => component.Label, "Stats for nerds"));

        var toggle = cut.Find("[role='switch']");
        var label = cut.Find(".sk-switch-label");

        Assert.Equal("false", toggle.GetAttribute("aria-checked"));
        Assert.Equal(label.GetAttribute("id"), toggle.GetAttribute("aria-labelledby"));
        Assert.Equal("Stats for nerds", label.TextContent);
        Assert.Contains("sk-switch", toggle.ClassList);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Switch_reports_the_inverse_of_the_current_state(bool checkedState, bool expected)
    {
        using var context = new BunitContext();
        var requested = !expected;
        var cut = context.Render<Switch>(parameters => parameters
            .Add(component => component.Checked, checkedState)
            .Add(component => component.Label, "Stats")
            .Add(component => component.OnChange,
                EventCallback.Factory.Create<bool>(this, value => requested = value)));

        cut.Find("[role='switch']").Click();

        Assert.Equal(expected, requested);
    }
}
