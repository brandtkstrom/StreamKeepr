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
    public void Signed_out_delete_is_inert_behind_requires_twitch()
    {
        using BunitContext context = CreateContext(out _, signedIn: false);

        var cut = Render(context, Recording());

        Assert.NotNull(cut.Find(".sk-requires-twitch[inert] button[data-delete]"));
    }

    [Fact]
    public void A_live_recording_disables_delete_with_a_reason()
    {
        using BunitContext context = CreateContext(out _, signedIn: true);

        var cut = Render(context, Recording(RecordingState.Recording));

        Assert.Contains("finishes", cut.Find("[data-delete-blocked]").TextContent);
        Assert.True(cut.Find("button[data-delete]").HasAttribute("disabled"));
    }

    [Fact]
    public void An_active_export_disables_delete_with_a_reason()
    {
        using BunitContext context = CreateContext(out _, signedIn: true);
        var exports = (TestExportService)context.Services.GetRequiredService<IExportService>();
        exports.Set(new ExportJob("e1", "r1", ExportState.Running, 0.4, null, null));

        var cut = Render(context, Recording());

        Assert.Contains("export", cut.Find("[data-delete-blocked]").TextContent);
        Assert.True(cut.Find("button[data-delete]").HasAttribute("disabled"));
    }

    [Fact]
    public void Delete_opens_a_named_dialog_and_cancel_closes_it()
    {
        using BunitContext context = CreateContext(out TestRecordingAdmin admin, out _, signedIn: true);
        var cut = Render(context, Recording());

        cut.Find("button[data-delete]").Click();

        var dialog = cut.FindComponent<Dialog>();
        Assert.True(dialog.Instance.Open);
        Assert.Contains("Title — ann", cut.Find("[data-delete-banner]").TextContent);
        Assert.Contains("permanently", cut.Find("[data-delete-banner]").TextContent);
        Assert.Empty(admin.Deletes);

        cut.Find("[data-delete-cancel]").Click();
        Assert.False(cut.FindComponent<Dialog>().Instance.Open);
    }

    [Fact]
    public void Confirm_calls_the_admin_and_closes_the_dialog()
    {
        using BunitContext context = CreateContext(out TestRecordingAdmin admin, out _, signedIn: true);
        var cut = Render(context, Recording());
        cut.Find("button[data-delete]").Click();

        cut.Find("[data-delete-confirm]").Click();

        var deleted = Assert.Single(admin.Deletes);
        Assert.Equal("r1", deleted.RecordingId);
        Assert.True(deleted.User.Identity?.IsAuthenticated);
        Assert.False(cut.FindComponent<Dialog>().Instance.Open);
    }

    [Fact]
    public void The_dialog_is_busy_while_delete_is_running()
    {
        using BunitContext context = CreateContext(out TestRecordingAdmin admin, out _, signedIn: true);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        admin.Gate = gate;
        var cut = Render(context, Recording());
        cut.Find("button[data-delete]").Click();

        cut.Find("[data-delete-confirm]").Click();

        Assert.True(cut.Find("[data-delete-confirm]").HasAttribute("disabled"));
        Assert.True(cut.Find("[data-delete-cancel]").HasAttribute("disabled"));
        Assert.Equal("true", cut.Find("[data-delete-dialog]").GetAttribute("aria-busy"));

        gate.SetResult();
        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<Dialog>().Instance.Open));
    }

    [Fact]
    public void Changing_recording_closes_an_open_dialog()
    {
        using BunitContext context = CreateContext(out _, signedIn: true);
        var cut = Render(context, Recording());
        cut.Find("button[data-delete]").Click();
        Assert.True(cut.FindComponent<Dialog>().Instance.Open);

        cut.Render(parameters => parameters.Add(
            component => component.Recording,
            Recording(id: "r2", title: "Next")));

        Assert.False(cut.FindComponent<Dialog>().Instance.Open);
    }

    [Fact]
    public void A_server_refusal_keeps_the_dialog_open_with_the_message()
    {
        using BunitContext context = CreateContext(out TestRecordingAdmin admin, out _, signedIn: true);
        var exports = (TestExportService)context.Services.GetRequiredService<IExportService>();
        admin.Failure = _ =>
        {
            exports.Set(new ExportJob("e1", "r1", ExportState.Running, 0.4, null, null));
            return new InvalidOperationException("That recording is being exported.");
        };
        var cut = Render(context, Recording());
        cut.Find("button[data-delete]").Click();

        cut.Find("[data-delete-confirm]").Click();

        Assert.True(cut.FindComponent<Dialog>().Instance.Open);
        Assert.Contains("being exported", cut.Find("[data-delete-banner]").TextContent);
        cut.WaitForAssertion(() =>
            Assert.True(cut.Find("[data-delete-confirm]").HasAttribute("disabled")));
    }

    [Fact]
    public void An_already_deleted_recording_closes_with_a_notice()
    {
        using BunitContext context = CreateContext(out TestRecordingAdmin admin, out SnackbarService snackbars, signedIn: true);
        admin.Failure = _ => new KeyNotFoundException("That recording was already deleted.");
        var cut = Render(context, Recording());
        cut.Find("button[data-delete]").Click();

        cut.Find("[data-delete-confirm]").Click();

        Assert.False(cut.FindComponent<Dialog>().Instance.Open);
        Assert.Contains("already deleted", snackbars.Current?.Text);
    }

    [Fact]
    public void Unauthorized_closes_with_the_reconnect_message()
    {
        using BunitContext context = CreateContext(out TestRecordingAdmin admin, out SnackbarService snackbars, signedIn: true);
        admin.Failure = _ => new UnauthorizedAccessException("Connect Twitch to delete recordings.");
        var cut = Render(context, Recording());
        cut.Find("button[data-delete]").Click();

        cut.Find("[data-delete-confirm]").Click();

        Assert.False(cut.FindComponent<Dialog>().Instance.Open);
        Assert.Contains("Reconnect to delete recordings", snackbars.Current?.Text);
    }

    private static BunitContext CreateContext(
        out TestRecordingAdmin admin,
        out SnackbarService snackbars,
        bool signedIn)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();
        context.Services.AddSingleton<IClientStateStore>(new TestClientStateStore());
        context.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        context.Services.AddScoped<ViewerClock>();
        context.Services.AddScoped<UiPreferences>();
        context.Services.AddScoped<SnackbarService>();
        context.Services.AddScoped<StreamKeepr.Components.Features.Player.PlayerBridge>();
        context.Services.AddSingleton<IExportService>(new TestExportService());
        admin = new TestRecordingAdmin();
        context.Services.AddSingleton<IRecordingAdmin>(admin);
        BunitAuthorizationContext auth = context.AddAuthorization();
        if (signedIn)
            auth.SetAuthorized("mock_admin");
        else
            auth.SetNotAuthorized();
        snackbars = context.Services.GetRequiredService<SnackbarService>();
        return context;
    }

    private static BunitContext CreateContext(
        out TestRecordingAdmin admin,
        bool signedIn) =>
        CreateContext(out admin, out _, signedIn);

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
