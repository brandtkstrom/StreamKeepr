using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Components.Primitives;

namespace StreamKeepr.Tests.Ui;

public sealed class MenuTests
{
    [Fact]
    public void Trigger_exposes_the_menu_relationship_and_starts_closed()
    {
        using var context = CreateContext();
        var cut = RenderMenu(context);

        var trigger = cut.Find("[data-menu-trigger]");
        var menu = cut.Find("[role='menu']");

        Assert.Equal("menu", trigger.GetAttribute("aria-haspopup"));
        Assert.Equal("false", trigger.GetAttribute("aria-expanded"));
        Assert.Equal(menu.GetAttribute("id"), trigger.GetAttribute("aria-controls"));
        Assert.Equal("Library options", trigger.GetAttribute("aria-label"));
        Assert.True(menu.HasAttribute("hidden"));
        Assert.All(cut.FindAll("[role='menuitem']"),
            item => Assert.Equal("-1", item.GetAttribute("tabindex")));
    }

    [Fact]
    public void Clicking_the_trigger_opens_the_menu()
    {
        using var context = CreateContext();
        var cut = RenderMenu(context);

        cut.Find("[data-menu-trigger]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("true", cut.Find("[data-menu-trigger]").GetAttribute("aria-expanded"));
            Assert.False(cut.Find("[role='menu']").HasAttribute("hidden"));
        });
    }

    [Fact]
    public void Selecting_an_item_invokes_its_callback_and_closes_the_menu()
    {
        using var context = CreateContext();
        var selected = "";
        var cut = RenderMenu(context, firstClick:
            EventCallback.Factory.Create(this, () => selected = "first"));

        cut.Find("[data-menu-trigger]").Click();
        cut.WaitForAssertion(() => Assert.False(cut.Find("[role='menu']").HasAttribute("hidden")));

        cut.FindAll("[role='menuitem']")[0].Click();

        Assert.Equal("first", selected);
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("false", cut.Find("[data-menu-trigger]").GetAttribute("aria-expanded"));
            Assert.True(cut.Find("[role='menu']").HasAttribute("hidden"));
        });
    }

    [Fact]
    public void Escape_closes_the_menu()
    {
        using var context = CreateContext();
        var cut = RenderMenu(context);

        cut.Find("[data-menu-trigger]").Click();
        cut.WaitForAssertion(() => Assert.False(cut.Find("[role='menu']").HasAttribute("hidden")));

        cut.Find("[role='menu']").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("false", cut.Find("[data-menu-trigger]").GetAttribute("aria-expanded"));
            Assert.True(cut.Find("[role='menu']").HasAttribute("hidden"));
        });
    }

    [Fact]
    public void Arrow_keys_keep_the_menu_open_and_request_item_focus()
    {
        using var context = CreateContext();
        var cut = RenderMenu(context);

        cut.Find("[data-menu-trigger]").Click();
        cut.WaitForAssertion(() => Assert.False(cut.Find("[role='menu']").HasAttribute("hidden")));

        cut.Find("[role='menu']").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        Assert.Equal("true", cut.Find("[data-menu-trigger]").GetAttribute("aria-expanded"));
        Assert.Contains(context.JSInterop.Invocations,
            invocation => invocation.Identifier.Contains("focus", StringComparison.Ordinal));
    }

    [Fact]
    public void Disabled_items_do_not_invoke_their_callback()
    {
        using var context = CreateContext();
        var clicked = false;
        var cut = RenderMenu(context,
            secondClick: EventCallback.Factory.Create(this, () => clicked = true),
            disabledSecond: true);

        cut.Find("[data-menu-trigger]").Click();
        cut.WaitForAssertion(() => Assert.False(cut.Find("[role='menu']").HasAttribute("hidden")));

        cut.FindAll("[role='menuitem']")[1].Click();

        Assert.False(clicked);
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();
        return context;
    }

    private static IRenderedComponent<Menu> RenderMenu(
        BunitContext context,
        EventCallback firstClick = default,
        EventCallback secondClick = default,
        bool disabledSecond = false) =>
        context.Render<Menu>(parameters => parameters
            .Add(component => component.Label, "Library options")
            .Add(component => component.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<MenuItem>(0);
                builder.AddAttribute(1, nameof(MenuItem.Icon), "arrow_downward");
                builder.AddAttribute(2, nameof(MenuItem.OnClick), firstClick);
                builder.AddAttribute(3, nameof(MenuItem.ChildContent),
                    (RenderFragment)(content => content.AddContent(0, "Newest first")));
                builder.CloseComponent();

                builder.OpenComponent<MenuItem>(4);
                builder.AddAttribute(5, nameof(MenuItem.Disabled), disabledSecond);
                builder.AddAttribute(6, nameof(MenuItem.OnClick), secondClick);
                builder.AddAttribute(7, nameof(MenuItem.ChildContent),
                    (RenderFragment)(content => content.AddContent(0, "Oldest first")));
                builder.CloseComponent();
            })));
}
