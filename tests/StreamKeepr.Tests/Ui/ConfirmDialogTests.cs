using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using StreamKeepr.Components.Primitives;

namespace StreamKeepr.Tests.Ui;

public sealed class ConfirmDialogTests
{
    [Fact]
    public async Task It_is_a_dialog_named_by_its_title_with_cancel_before_the_confirm()
    {
        await using var context = CreateContext();
        var dialogs = context.Render<MudDialogProvider>();
        Render(context, busy: false, _ => { });

        Assert.True(dialogs.HasOpenDialog("Delete recording?"));
        var buttons = dialogs.FindAll("[data-test-dialog] button").Select(button => button.TextContent.Trim());
        Assert.Equal(["Cancel", "Delete"], buttons);
        Assert.NotNull(dialogs.Find("button[data-test-cancel]"));
        Assert.NotNull(dialogs.Find("button[data-test-confirm]"));
        Assert.Null(dialogs.Find("[data-test-dialog]").GetAttribute("aria-busy"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Escape_or_the_backdrop_asks_the_owner_to_close_an_idle_dialog(bool escape)
    {
        await using var context = CreateContext();
        var dialogs = context.Render<MudDialogProvider>();
        var requests = new List<bool>();
        Render(context, busy: false, requests.Add);

        await DismissAsync(dialogs, escape);

        Assert.Equal([false], requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task While_busy_escape_and_the_backdrop_are_ignored(bool escape)
    {
        await using var context = CreateContext();
        var dialogs = context.Render<MudDialogProvider>();
        var requests = new List<bool>();
        Render(context, busy: true, requests.Add);

        await DismissAsync(dialogs, escape);

        Assert.Empty(requests);
        Assert.True(dialogs.HasOpenDialog("Delete recording?"));
        Assert.Equal("true", dialogs.Find("[data-test-dialog]").GetAttribute("aria-busy"));
        Assert.All(dialogs.FindAll("[data-test-dialog] button"), button => Assert.True(button.HasAttribute("disabled")));
    }

    [Fact]
    public async Task Other_keys_leave_the_dialog_open()
    {
        await using var context = CreateContext();
        var dialogs = context.Render<MudDialogProvider>();
        var requests = new List<bool>();
        Render(context, busy: false, requests.Add);

        await dialogs.Find("[data-test-dialog]").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        Assert.Empty(requests);
    }

    // Escape from inside the confirmation (where Cancel has focus), and a click on the backdrop
    // beside the dialog.
    private static Task DismissAsync(IRenderedComponent<MudDialogProvider> dialogs, bool escape) =>
        escape
            ? dialogs.Find("[data-test-dialog]").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" })
            : dialogs.Find("[role='dialog']").ParentElement!.QuerySelector("[aria-hidden='true']")!.ClickAsync(new MouseEventArgs());

    private static IRenderedComponent<ConfirmDialog> Render(BunitContext context, bool busy, Action<bool> onVisibleChanged) =>
        context.Render<ConfirmDialog>(parameters => parameters
            .Add(component => component.Visible, true)
            .Add(component => component.VisibleChanged, EventCallback.Factory.Create(new object(), onVisibleChanged))
            .Add(component => component.Title, "Delete recording?")
            .Add(component => component.ConfirmLabel, "Delete")
            .Add(component => component.HookPrefix, "test")
            .Add(component => component.Busy, busy)
            .AddChildContent("<p>The files are removed permanently.</p>"));

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }
}
