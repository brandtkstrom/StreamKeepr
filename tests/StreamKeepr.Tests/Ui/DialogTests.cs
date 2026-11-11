using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Components.Primitives;

namespace StreamKeepr.Tests.Ui;

public sealed class DialogTests
{
    [Fact]
    public void Renders_a_native_dialog_with_a_labelled_title_content_and_actions()
    {
        using var context = CreateContext();
        var cut = RenderDialog(context, open: false);

        var dialog = cut.Find("dialog.sk-dialog");
        var title = cut.Find(".sk-dialog h2");

        Assert.False(dialog.HasAttribute("open"));
        Assert.Equal(title.GetAttribute("id"), dialog.GetAttribute("aria-labelledby"));
        Assert.Equal("Delete recording?", title.TextContent);
        Assert.Contains("This cannot be undone", cut.Find(".sk-dialog-content").TextContent);
        Assert.Equal("Cancel", cut.Find(".sk-dialog-actions button").TextContent);
    }

    [Fact]
    public async Task Esc_requests_a_close_and_raises_the_close_callback()
    {
        using var context = CreateContext();
        var open = true;
        var closed = false;
        var cut = context.Render<Dialog>(parameters => parameters
            .Add(component => component.Open, true)
            .Add(component => component.Title, "Delete recording?")
            .Add(component => component.OpenChanged,
                EventCallback.Factory.Create<bool>(this, value => open = value))
            .Add(component => component.OnClose,
                EventCallback.Factory.Create(this, () => closed = true)));

        await cut.InvokeAsync(() => cut.Instance.OnDialogCancel());

        Assert.False(open);
        Assert.True(closed);
    }

    [Fact]
    public async Task Esc_does_not_close_an_already_closed_dialog()
    {
        using var context = CreateContext();
        var closed = false;
        var cut = RenderDialog(context, open: false, onClose: () => closed = true);

        await cut.InvokeAsync(() => cut.Instance.OnDialogCancel());

        Assert.False(closed);
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();
        return context;
    }

    private static IRenderedComponent<Dialog> RenderDialog(
        BunitContext context,
        bool open,
        Action? onClose = null) =>
        context.Render<Dialog>(parameters => parameters
            .Add(component => component.Open, open)
            .Add(component => component.Title, "Delete recording?")
            .Add(component => component.ChildContent,
                (RenderFragment)(content =>
                    content.AddMarkupContent(0, "<p>This cannot be undone.</p>")))
            .Add(component => component.Actions, (RenderFragment)(actions =>
            {
                actions.OpenElement(0, "button");
                actions.AddAttribute(1, "type", "button");
                actions.AddContent(2, "Cancel");
                actions.CloseElement();
            }))
            .Add(component => component.OnClose,
                EventCallback.Factory.Create(context, () => onClose?.Invoke())));
}
