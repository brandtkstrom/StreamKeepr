using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Components.Features.Details;
using StreamKeepr.Components.Features.Player;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

/// <summary>The MudBlazor details layout (#178): title row, meta line and the compact actions menu.</summary>
public sealed class RecordingDetailsLayoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_meta_line_shows_the_duration()
    {
        await using BunitContext context = CreateContext(signedIn: false);

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        Assert.Equal("2h 14m", cut.Find("[data-details-duration]").TextContent.Trim());
    }

    [Theory]
    [InlineData(RecordingState.Recording, "rec")]
    [InlineData(RecordingState.Reconnecting, "reconnecting")]
    public async Task A_live_recording_leads_the_meta_line_with_its_live_chip(RecordingState state, string status)
    {
        await using BunitContext context = CreateContext(signedIn: false);

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording(state));

        IElement chip = cut.Find("[data-details-meta] [data-status]");
        Assert.Equal(status, chip.GetAttribute("data-status"));
        // First on the line: it comes before the streamer, the first of the meta pairs.
        IElement streamer = cut.Find("[data-details-streamer]");
        Assert.True(chip.CompareDocumentPosition(streamer).HasFlag(DocumentPositions.Following));
    }

    [Fact]
    public async Task A_finished_recording_has_no_live_chip()
    {
        await using BunitContext context = CreateContext(signedIn: false);

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        Assert.Empty(cut.FindAll("[data-details-meta] [data-status]"));
    }

    [Fact]
    public async Task The_full_title_is_offered_in_a_tooltip_on_the_title()
    {
        await using BunitContext context = CreateContext(signedIn: false);
        const string title = "A very long stream title that wraps to two lines before it is cut short";

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording(title: title));

        var tooltip = cut.FindComponents<MudBlazor.MudTooltip>()
            .Single(component => component.FindAll("[data-details-title]").Count == 1);
        Assert.Equal(title, tooltip.Instance.Text);
        Assert.Equal(title, cut.Find("[data-details-title]").TextContent);
    }

    [Fact]
    public async Task Keyboard_focus_on_the_title_shows_the_full_title()
    {
        await using BunitContext context = CreateContext(signedIn: false);
        var popovers = context.Render<MudBlazor.MudPopoverProvider>();
        const string title = "A very long stream title that wraps to two lines before it is cut short";
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording(title: title));
        Assert.DoesNotContain(title, popovers.Markup);

        IElement heading = cut.Find("[data-details-title]");
        Assert.Equal("0", heading.GetAttribute("tabindex"));
        await heading.FocusInAsync(new Microsoft.AspNetCore.Components.Web.FocusEventArgs());

        popovers.WaitForAssertion(() => Assert.Contains(title, popovers.Markup));
    }

    [Fact]
    public async Task The_title_is_a_heading_below_the_panel_heading()
    {
        await using BunitContext context = CreateContext(signedIn: false);

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        Assert.Equal("H3", cut.Find("[data-details-title]").TagName);
        Assert.Equal("Recording details", cut.Find("[data-collapsible-toggle] h2").TextContent.Trim());
    }

    [Fact]
    public async Task More_actions_offers_stop_and_a_disabled_delete_while_live()
    {
        await using BunitContext context = CreateContext(signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        var popovers = context.Render<MudBlazor.MudPopoverProvider>();
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording(RecordingState.Recording));

        cut.Find("button[aria-label='More actions']").Click();

        popovers.WaitForAssertion(() => Assert.Equal(2, popovers.FindAll("[role='menuitem']").Count));
        IElement delete = MenuItem(popovers, "Delete recording");
        Assert.Equal("true", delete.GetAttribute("aria-disabled"));
        Assert.Contains("Available when the recording finishes", delete.TextContent);

        MenuItem(popovers, "Stop recording").Click();
        dialogs.WaitForAssertion(() => Assert.True(dialogs.HasOpenDialog("Stop recording for ann?")));
    }

    [Fact]
    public async Task More_actions_deletes_a_finished_recording_through_the_dialog()
    {
        await using BunitContext context = CreateContext(signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        var popovers = context.Render<MudBlazor.MudPopoverProvider>();
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        cut.Find("button[aria-label='More actions']").Click();

        popovers.WaitForAssertion(() => Assert.Single(popovers.FindAll("[role='menuitem']")));
        MenuItem(popovers, "Delete recording").Click();
        dialogs.WaitForAssertion(() => Assert.True(dialogs.HasOpenDialog("Delete recording?")));
    }

    [Fact]
    public async Task Signed_out_more_actions_is_inert_behind_requires_twitch()
    {
        await using BunitContext context = CreateContext(signedIn: false);

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        Assert.NotNull(cut.Find("[data-requires-twitch] [inert] button[aria-label='More actions']"));
    }

    [Fact]
    public async Task A_running_export_announces_its_state_but_not_each_percent()
    {
        await using BunitContext context = CreateContext(signedIn: true);
        var exports = (TestExportService)context.Services.GetRequiredService<IExportService>();
        exports.Set(new ExportJob("e1", "r1", ExportState.Running, 0.42, null, null));

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        Assert.Equal("Exporting…", cut.Find("[data-export-row] [role='status']").TextContent);
        IElement bar = cut.Find("[data-export-row] [role='progressbar']");
        Assert.Equal("Export progress", bar.GetAttribute("aria-label"));
        Assert.Equal("off", bar.GetAttribute("aria-live"));
        Assert.Empty(bar.QuerySelectorAll("[aria-live]:not([aria-live='off'])"));
    }

    [Fact]
    public async Task The_phone_summary_moves_the_live_chip_to_the_actions_and_trims_the_meta()
    {
        await using BunitContext context = CreateContext(signedIn: false);

        IRenderedComponent<RecordingDetailsSummary> cut = context.Render<RecordingDetailsSummary>(p => p
            .Add(component => component.Recording, Recording(RecordingState.Recording))
            .Add(component => component.Compact, true));

        Assert.Empty(cut.FindAll("[data-details-meta] [data-status]"));
        Assert.NotNull(cut.Find("[data-details-actions] [data-status]"));
        Assert.Empty(cut.FindAll("[data-details-category]"));
        Assert.Empty(cut.FindAll("[data-details-counts]"));
    }

    private static IElement MenuItem(IRenderedComponent<MudBlazor.MudPopoverProvider> popovers, string label) =>
        popovers.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim().StartsWith(label, StringComparison.Ordinal));

    private static IRenderedComponent<RecordingDetails> Render(BunitContext context, RecordingView recording) =>
        context.Render<RecordingDetails>(parameters => parameters.Add(component => component.Recording, recording));

    private static BunitContext CreateContext(bool signedIn)
    {
        var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();
        context.Services.AddSingleton<IClientStateStore>(new TestClientStateStore());
        context.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        context.Services.AddScoped<ViewerClock>();
        context.Services.AddScoped<UiPreferences>();
        context.Services.AddScoped<PlayerBridge>();
        context.Services.AddSingleton<IExportService>(new TestExportService());
        context.Services.AddSingleton<IRecordingAdmin>(new TestRecordingAdmin());
        context.Services.AddSingleton<IChannelsView>(new TestChannelsView());
        BunitAuthorizationContext auth = context.AddAuthorization();
        if (signedIn)
            auth.SetAuthorized("mock_admin");
        else
            auth.SetNotAuthorized();
        return context;
    }

    private static RecordingView Recording(RecordingState state = RecordingState.Saved, string title = "Title") =>
        new(
            "r1",
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
