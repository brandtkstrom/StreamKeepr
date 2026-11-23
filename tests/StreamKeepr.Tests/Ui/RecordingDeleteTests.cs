using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Components.Features.Details;
using StreamKeepr.Components.Features.Player;
using StreamKeepr.Components.Primitives;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class RecordingDeleteTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Signed_out_delete_is_inert_behind_requires_twitch()
    {
        await using BunitContext context = CreateContext(out _, signedIn: false);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();

        var cut = Render(context, Recording());

        Assert.NotNull(cut.Find("[data-requires-twitch] [inert] button[aria-label='More actions']"));
    }

    [Fact]
    public async Task A_live_recording_disables_delete_with_a_reason()
    {
        await using BunitContext context = CreateContext(out _, signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();

        var cut = Render(context, Recording(RecordingState.Recording));

        AngleSharp.Dom.IElement delete = MoreActions.Item(context, cut, "data-delete");
        Assert.Contains("finishes", delete.QuerySelector("[data-delete-blocked]")!.TextContent);
        Assert.Equal("true", delete.GetAttribute("aria-disabled"));
    }

    [Fact]
    public async Task An_active_export_disables_delete_with_a_reason()
    {
        await using BunitContext context = CreateContext(out _, signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        var exports = (TestExportService)context.Services.GetRequiredService<IExportService>();
        exports.Set(new ExportJob("e1", "r1", ExportState.Running, 0.4, null, null));

        var cut = Render(context, Recording());

        AngleSharp.Dom.IElement delete = MoreActions.Item(context, cut, "data-delete");
        Assert.Contains("export", delete.QuerySelector("[data-delete-blocked]")!.TextContent);
        Assert.Equal("true", delete.GetAttribute("aria-disabled"));
    }

    [Fact]
    public async Task Delete_opens_a_named_dialog_and_cancel_closes_it()
    {
        await using BunitContext context = CreateContext(out TestRecordingAdmin admin, out _, signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        var cut = Render(context, Recording());

        MoreActions.Item(context, cut, "data-delete").Click();
        dialogs.WaitForAssertion(() => Assert.True(dialogs.HasOpenDialog(DeleteName)));
        Assert.True(dialogs.HasOpenDialog(DeleteName));
        Assert.Contains("Title — ann", dialogs.Find("[data-delete-banner]").TextContent);
        Assert.Contains("permanently", dialogs.Find("[data-delete-banner]").TextContent);
        Assert.Empty(admin.Deletes);

        dialogs.Find("[data-delete-cancel]").Click();
        Assert.False(dialogs.HasOpenDialog(DeleteName));
    }

    [Fact]
    public async Task Cancel_hands_focus_back_to_more_actions()
    {
        await using BunitContext context = CreateContext(out _, out _, signedIn: true);
        var module = context.JSInterop.SetupModule("./Components/Features/Details/RecordingDetails.razor.js");
        module.SetupVoid("focusButton", _ => true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        var cut = Render(context, Recording());
        MoreActions.Item(context, cut, "data-delete").Click();
        dialogs.WaitForAssertion(() => Assert.True(dialogs.HasOpenDialog(DeleteName)));
        Assert.Empty(module.Invocations);

        dialogs.Find("[data-delete-cancel]").Click();

        cut.WaitForAssertion(() => Assert.Single(module.Invocations, invocation => invocation.Identifier == "focusButton"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Escape_or_the_backdrop_closes_an_idle_dialog(bool escape)
    {
        await using BunitContext context = CreateContext(out TestRecordingAdmin admin, out _, signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        var cut = Render(context, Recording());
        MoreActions.Item(context, cut, "data-delete").Click();
        dialogs.WaitForAssertion(() => Assert.True(dialogs.HasOpenDialog(DeleteName)));

        if (escape)
            await PressEscapeAsync(dialogs);
        else
            await ClickBackdropAsync(dialogs);

        dialogs.WaitForAssertion(() => Assert.False(dialogs.HasOpenDialog(DeleteName)));
        Assert.Empty(admin.Deletes);
    }

    [Fact]
    public async Task Confirm_calls_the_admin_and_closes_the_dialog()
    {
        await using BunitContext context = CreateContext(out TestRecordingAdmin admin, out _, signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        var cut = Render(context, Recording());
        MoreActions.Item(context, cut, "data-delete").Click();
        dialogs.WaitForAssertion(() => Assert.True(dialogs.HasOpenDialog(DeleteName)));

        dialogs.Find("[data-delete-confirm]").Click();

        var deleted = Assert.Single(admin.Deletes);
        Assert.Equal("r1", deleted.RecordingId);
        Assert.True(deleted.User.Identity?.IsAuthenticated);
        Assert.False(dialogs.HasOpenDialog(DeleteName));
    }

    [Fact]
    public async Task The_dialog_is_busy_while_delete_is_running()
    {
        await using BunitContext context = CreateContext(out TestRecordingAdmin admin, out _, signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        admin.Gate = gate;
        var cut = Render(context, Recording());
        MoreActions.Item(context, cut, "data-delete").Click();
        dialogs.WaitForAssertion(() => Assert.True(dialogs.HasOpenDialog(DeleteName)));

        dialogs.Find("[data-delete-confirm]").Click();

        Assert.True(dialogs.Find("[data-delete-confirm]").HasAttribute("disabled"));
        Assert.True(dialogs.Find("[data-delete-cancel]").HasAttribute("disabled"));
        Assert.Equal("true", dialogs.Find("[data-delete-dialog]").GetAttribute("aria-busy"));
        // Escape and the backdrop are ignored while the server call runs.
        await PressEscapeAsync(dialogs);
        await ClickBackdropAsync(dialogs);
        Assert.True(dialogs.HasOpenDialog(DeleteName));

        gate.SetResult();
        dialogs.WaitForAssertion(() => Assert.False(dialogs.HasOpenDialog(DeleteName)));
    }

    [Fact]
    public async Task Changing_recording_closes_an_open_dialog()
    {
        await using BunitContext context = CreateContext(out _, signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        var cut = Render(context, Recording());
        MoreActions.Item(context, cut, "data-delete").Click();
        dialogs.WaitForAssertion(() => Assert.True(dialogs.HasOpenDialog(DeleteName)));
        Assert.True(dialogs.HasOpenDialog(DeleteName));

        cut.Render(parameters => parameters.Add(
            component => component.Recording,
            Recording(id: "r2", title: "Next")));

        Assert.False(dialogs.HasOpenDialog(DeleteName));
    }

    [Fact]
    public async Task A_server_refusal_keeps_the_dialog_open_with_the_message()
    {
        await using BunitContext context = CreateContext(out TestRecordingAdmin admin, out _, signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        var exports = (TestExportService)context.Services.GetRequiredService<IExportService>();
        admin.Failure = _ =>
        {
            exports.Set(new ExportJob("e1", "r1", ExportState.Running, 0.4, null, null));
            return new InvalidOperationException("That recording is being exported.");
        };
        var cut = Render(context, Recording());
        MoreActions.Item(context, cut, "data-delete").Click();
        dialogs.WaitForAssertion(() => Assert.True(dialogs.HasOpenDialog(DeleteName)));

        dialogs.Find("[data-delete-confirm]").Click();

        // The refusal comes back through the async delete, so the banner may render a moment
        // after the click on a slow machine (CI, PR #262).
        dialogs.WaitForAssertion(() =>
            Assert.Contains("being exported", dialogs.Find("[data-delete-banner]").TextContent));
        Assert.True(dialogs.HasOpenDialog(DeleteName));
        cut.WaitForAssertion(() =>
            Assert.True(dialogs.Find("[data-delete-confirm]").HasAttribute("disabled")));
    }

    [Fact]
    public async Task An_already_deleted_recording_closes_with_a_notice()
    {
        await using BunitContext context = CreateContext(out TestRecordingAdmin admin, out MudBlazor.ISnackbar snackbars, signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        admin.Failure = _ => new KeyNotFoundException("That recording was already deleted.");
        var cut = Render(context, Recording());
        MoreActions.Item(context, cut, "data-delete").Click();
        dialogs.WaitForAssertion(() => Assert.True(dialogs.HasOpenDialog(DeleteName)));

        dialogs.Find("[data-delete-confirm]").Click();

        Assert.False(dialogs.HasOpenDialog(DeleteName));
        Assert.Contains("already deleted", snackbars.CurrentMessage());
    }

    [Fact]
    public async Task Unauthorized_closes_with_the_reconnect_message()
    {
        await using BunitContext context = CreateContext(out TestRecordingAdmin admin, out MudBlazor.ISnackbar snackbars, signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        admin.Failure = _ => new UnauthorizedAccessException("Connect Twitch to delete recordings.");
        var cut = Render(context, Recording());
        MoreActions.Item(context, cut, "data-delete").Click();
        dialogs.WaitForAssertion(() => Assert.True(dialogs.HasOpenDialog(DeleteName)));

        dialogs.Find("[data-delete-confirm]").Click();

        Assert.False(dialogs.HasOpenDialog(DeleteName));
        Assert.Contains("Reconnect to delete recordings", snackbars.CurrentMessage());
    }

    private static BunitContext CreateContext(
        out TestRecordingAdmin admin,
        out MudBlazor.ISnackbar snackbars,
        bool signedIn)
    {
        var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();
        context.Services.AddSingleton<IClientStateStore>(new TestClientStateStore());
        context.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        context.Services.AddScoped<ViewerClock>();
        context.Services.AddScoped<UiPreferences>();
        context.Services.AddScoped<StreamKeepr.Components.Features.Player.PlayerBridge>();
        context.Services.AddSingleton<IExportService>(new TestExportService());
        admin = new TestRecordingAdmin();
        context.Services.AddSingleton<IRecordingAdmin>(admin);
        context.Services.AddSingleton<IChannelsView>(new TestChannelsView());
        BunitAuthorizationContext auth = context.AddAuthorization();
        if (signedIn)
            auth.SetAuthorized("mock_admin");
        else
            auth.SetNotAuthorized();
        snackbars = context.Services.GetRequiredService<MudBlazor.ISnackbar>();
        return context;
    }

    private static BunitContext CreateContext(
        out TestRecordingAdmin admin,
        bool signedIn) =>
        CreateContext(out admin, out _, signedIn);

    private const string DeleteName = "Delete recording?";

    // Escape from inside the confirmation (where Cancel has focus), and a click on the
    // backdrop beside the dialog.
    private static Task PressEscapeAsync(IRenderedComponent<MudBlazor.MudDialogProvider> dialogs) =>
        dialogs.Find("[data-delete-dialog]").KeyDownAsync(
            new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

    private static Task ClickBackdropAsync(IRenderedComponent<MudBlazor.MudDialogProvider> dialogs) =>
        dialogs.Find("[role='dialog']").ParentElement!.QuerySelector("[aria-hidden='true']")!.ClickAsync(
            new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

    private static IRenderedComponent<RecordingDetails> Render(
        BunitContext context,
        RecordingView recording) =>
        context.Render<RecordingDetails>(parameters =>
            parameters.Add(component => component.Recording, recording));

    private static RecordingView Recording(
        RecordingState state = RecordingState.Saved,
        string id = "r1",
        string title = "Title") =>
        new(
            id,
            "ann",
            title,
            "Food",
            new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero),
            8047,
            state,
            SegmentCount: 1342,
            PartCount: 3,
            SizeBytes: 10_522_669_875,
            ThumbnailUrl: null,
            PlaylistUrl: "/media/ann/r1/index.m3u8",
            ResumeSec: null,
            Chapters: []);
}
