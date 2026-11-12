using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Components.Features.Details;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class RecordingExportTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_signed_in_admin_starts_an_export_and_sees_it_queued_then_running()
    {
        using BunitContext context = CreateContext(out TestExportService exports, signedIn: true);
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording("r1"));
        Assert.Empty(cut.FindAll("[data-export-row]"));

        cut.Find("button[data-export]").Click();

        (string recordingId, var user) = Assert.Single(exports.Starts);
        Assert.Equal("r1", recordingId);
        Assert.Equal("mock_admin", user.Identity?.Name);
        cut.WaitForAssertion(() =>
            Assert.Equal("Queued — waiting for another export", cut.Find("[data-export-row] [role='status']").TextContent));
        Assert.True(cut.Find("button[data-export]").HasAttribute("disabled"));

        cut.InvokeAsync(() => exports.Set(new ExportJob("e1", "r1", ExportState.Running, 0.42, null, null)));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Exporting…", cut.Find("[data-export-row] [role='status']").TextContent);
            var bar = cut.Find("[role='progressbar']");
            Assert.Equal("42", bar.GetAttribute("aria-valuenow"));
            Assert.Equal("42%", bar.GetAttribute("aria-valuetext"));
            Assert.Equal("0", bar.GetAttribute("aria-valuemin"));
            Assert.Equal("100", bar.GetAttribute("aria-valuemax"));
        });
    }

    [Fact]
    public void Success_shows_a_download_link_and_one_snackbar()
    {
        using BunitContext context = CreateContext(out TestExportService exports, signedIn: true);
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording("r1"));
        cut.Find("button[data-export]").Click();
        SnackbarService snackbars = context.Services.GetRequiredService<SnackbarService>();

        cut.InvokeAsync(() => exports.Set(new ExportJob("e1", "r1", ExportState.Succeeded, 1, "ann_x.mp4", null)));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("MP4 ready · ann_x.mp4", cut.Find("[data-export-row] [role='status']").TextContent);
            var download = cut.Find("a[data-export-download]");
            Assert.Equal("/api/exports/e1/download", download.GetAttribute("href"));
            Assert.Equal("false", download.GetAttribute("data-enhance-nav"));
        });
        Assert.Equal("Export ready", snackbars.Current?.Text);
        Assert.False(cut.Find("button[data-export]").HasAttribute("disabled"));

        snackbars.Dismiss();
        cut.InvokeAsync(() => exports.Set(new ExportJob("e1", "r1", ExportState.Succeeded, 1, "ann_x.mp4", null)));
        Assert.Null(snackbars.Current);
    }

    [Fact]
    public void The_row_rehydrates_from_find_by_recording_and_never_shows_another_recordings_job()
    {
        using BunitContext context = CreateContext(out TestExportService exports, signedIn: true);
        exports.Set(new ExportJob("e7", "r1", ExportState.Running, 0.5, null, null));

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording("r1"));
        Assert.Equal("Exporting…", cut.Find("[data-export-row] [role='status']").TextContent);

        cut.Render(p => p.Add(c => c.Recording, Recording("r2")));

        Assert.Empty(cut.FindAll("[data-export-row]"));
        Assert.False(cut.Find("button[data-export]").HasAttribute("disabled"));
    }

    [Fact]
    public void Export_is_disabled_while_live_with_an_explanation()
    {
        using BunitContext context = CreateContext(out TestExportService exports, signedIn: true);

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording("r1", RecordingState.Recording));

        Assert.True(cut.Find("button[data-export]").HasAttribute("disabled"));
        Assert.Equal("Available when the recording finishes", cut.Find("[role='tooltip']").TextContent);
        Assert.Empty(exports.Starts);
    }

    [Fact]
    public void Signed_out_the_export_action_is_inert_behind_requires_twitch()
    {
        using BunitContext context = CreateContext(out _, signedIn: false);

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording("r1"));

        Assert.NotNull(cut.Find(".sk-requires-twitch[inert] button[data-export]"));
    }

    [Fact]
    public void A_refused_start_shows_the_service_message()
    {
        using BunitContext context = CreateContext(out TestExportService exports, signedIn: true);
        exports.StartFailure = _ => new InvalidOperationException("The recording can be exported when it finishes.");
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording("r1"));

        cut.Find("button[data-export]").Click();

        Assert.Equal(
            "The recording can be exported when it finishes.",
            context.Services.GetRequiredService<SnackbarService>().Current?.Text);
        Assert.Empty(cut.FindAll("[data-export-row]"));
    }

    [Fact]
    public void Queued_and_running_jobs_offer_cancel_which_ends_in_cancelled_with_retry_and_no_snackbar()
    {
        using BunitContext context = CreateContext(out TestExportService exports, signedIn: true);
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording("r1"));
        cut.Find("button[data-export]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("button[data-export-cancel]")));
        cut.InvokeAsync(() => exports.Set(new ExportJob("e1", "r1", ExportState.Running, 0.3, null, null)));

        cut.WaitForAssertion(() => cut.Find("button[data-export-cancel]").Click());

        (string id, var user) = Assert.Single(exports.Cancels);
        Assert.Equal("e1", id);
        Assert.Equal("mock_admin", user.Identity?.Name);
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Export cancelled", cut.Find("[data-export-row] [role='status']").TextContent);
            Assert.NotNull(cut.Find("button[data-export-retry]"));
            Assert.Empty(cut.FindAll("button[data-export-cancel]"));
        });
        Assert.Null(context.Services.GetRequiredService<SnackbarService>().Current);

        cut.Find("button[data-export-retry]").Click();

        Assert.Equal(2, exports.Starts.Count);
        cut.WaitForAssertion(() =>
            Assert.Equal("Queued — waiting for another export", cut.Find("[data-export-row] [role='status']").TextContent));
    }

    [Fact]
    public void A_cancel_that_loses_the_race_with_completion_is_ignored()
    {
        using BunitContext context = CreateContext(out TestExportService exports, signedIn: true);
        exports.Set(new ExportJob("e1", "r1", ExportState.Running, 0.9, null, null));
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording("r1"));
        exports.CancelFailure = _ =>
        {
            exports.Set(new ExportJob("e1", "r1", ExportState.Succeeded, 1, "x.mp4", null));
            return new InvalidOperationException("The export has already finished.");
        };
        SnackbarService snackbars = context.Services.GetRequiredService<SnackbarService>();

        cut.Find("button[data-export-cancel]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("a[data-export-download]")));
        // The finished job announces itself; the refused cancel adds nothing.
        Assert.Equal("Export ready", snackbars.Current?.Text);
    }

    [Fact]
    public void A_failed_export_shows_the_safe_message_with_retry()
    {
        using BunitContext context = CreateContext(out TestExportService exports, signedIn: true);
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording("r1"));
        cut.Find("button[data-export]").Click();

        cut.InvokeAsync(() => exports.Set(
            new ExportJob("e1", "r1", ExportState.Failed, 0.2, null, "The export could not be completed.")));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("The export could not be completed.", cut.Find("[data-export-row] [role='status']").TextContent);
            Assert.NotNull(cut.Find("button[data-export-retry]"));
        });
        Assert.Equal("Export failed", context.Services.GetRequiredService<SnackbarService>().Current?.Text);
        Assert.False(cut.Find("button[data-export]").HasAttribute("disabled"));
    }

    [Fact]
    public void Incompatible_parts_explain_why_offer_no_retry_and_keep_export_disabled()
    {
        using BunitContext context = CreateContext(out TestExportService exports, signedIn: true);
        exports.Set(new ExportJob("e1", "r1", ExportState.Failed, 0, null, ExportService.IncompatibleParts));

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording("r1"));

        Assert.Equal(
            "This recording's parts cannot be joined into one MP4.",
            cut.Find("[data-export-row] [role='status']").TextContent);
        Assert.Empty(cut.FindAll("button[data-export-retry]"));
        Assert.True(cut.Find("button[data-export]").HasAttribute("disabled"));
        Assert.Equal(
            "Its parts use different video settings, so one MP4 is not possible",
            cut.Find("[role='tooltip']").TextContent);
    }

    private static IRenderedComponent<RecordingDetails> Render(BunitContext context, RecordingView recording) =>
        context.Render<RecordingDetails>(p => p.Add(c => c.Recording, recording));

    private static BunitContext CreateContext(out TestExportService exports, bool signedIn)
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
        exports = new TestExportService();
        context.Services.AddSingleton<IExportService>(exports);
        context.Services.AddSingleton<IRecordingAdmin>(new TestRecordingAdmin());
        BunitAuthorizationContext auth = context.AddAuthorization();
        if (signedIn)
            auth.SetAuthorized("mock_admin");
        else
            auth.SetNotAuthorized();
        return context;
    }

    private static RecordingView Recording(string id, RecordingState state = RecordingState.Saved) =>
        new(id, "ann", "Title", "Food", Now.AddHours(-2), Now.AddHours(-1), 3600, state, 10, 1, 1024,
            null, $"/media/ann/{id}/index.m3u8", null, []);
}
