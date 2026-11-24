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
    public async Task A_live_recording_places_its_live_chip_beside_more_actions(RecordingState state, string status)
    {
        await using BunitContext context = CreateContext(signedIn: false);

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording(state));

        Assert.Empty(cut.FindAll("[data-details-meta] [data-status]"));
        IElement chip = cut.Find("[data-details-actions] [data-status]");
        Assert.Equal(status, chip.GetAttribute("data-status"));
        IElement more = cut.Find("button[aria-label='More actions']");
        Assert.True(chip.CompareDocumentPosition(more).HasFlag(DocumentPositions.Following));
    }

    [Fact]
    public async Task A_finished_recording_has_no_live_chip()
    {
        await using BunitContext context = CreateContext(signedIn: false);

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        Assert.Empty(cut.FindAll("[data-details-actions] [data-status]"));
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
    public async Task The_title_is_the_pages_section_heading_outside_any_panel()
    {
        await using BunitContext context = CreateContext(signedIn: false);

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        Assert.Equal("H2", cut.Find("[data-details-title]").TagName);
        Assert.Empty(cut.FindAll("[data-collapsible-toggle]"));
    }

    [Fact]
    public async Task More_actions_offers_copy_export_stop_and_a_disabled_delete_while_live()
    {
        await using BunitContext context = CreateContext(signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        var popovers = context.Render<MudBlazor.MudPopoverProvider>();
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording(RecordingState.Recording));

        cut.Find("button[aria-label='More actions']").Click();

        popovers.WaitForAssertion(() => Assert.Equal(4, popovers.FindAll("[role='menuitem']").Count));
        IElement export = MenuItem(popovers, "Export MP4");
        Assert.Equal("true", export.GetAttribute("aria-disabled"));
        Assert.Contains("Available when the recording finishes", export.TextContent);
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

        popovers.WaitForAssertion(() => Assert.Equal(3, popovers.FindAll("[role='menuitem']").Count));
        MenuItem(popovers, "Delete recording").Click();
        dialogs.WaitForAssertion(() => Assert.True(dialogs.HasOpenDialog("Delete recording?")));
    }

    [Fact]
    public async Task The_streamer_avatar_shows_the_channels_twitch_image()
    {
        await using BunitContext context = CreateContext(signedIn: false);

        IRenderedComponent<RecordingDetails> cut = context.Render<RecordingDetails>(parameters => parameters
            .Add(component => component.Recording, Recording())
            .Add(component => component.AvatarUrl, "https://static-cdn.jtvnw.net/ann.png"));

        Assert.Equal("https://static-cdn.jtvnw.net/ann.png",
            cut.Find("[data-details-avatar] img[data-avatar-image]").GetAttribute("src"));
    }

    [Fact]
    public async Task Without_an_image_the_streamer_avatar_shows_the_initial()
    {
        await using BunitContext context = CreateContext(signedIn: false);

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        Assert.Empty(cut.FindAll("[data-details-avatar] img"));
        Assert.Equal("A", cut.Find("[data-details-avatar]").TextContent.Trim());
    }

    [Fact]
    public async Task Signed_out_more_actions_is_not_inert()
    {
        await using BunitContext context = CreateContext(signedIn: false);

        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        Assert.Empty(cut.FindAll("[data-requires-twitch] [inert] button[aria-label='More actions']"));
        Assert.Single(cut.FindAll("button[aria-label='More actions']"));
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
    public async Task Copy_and_export_move_into_more_actions_and_the_live_chip_sits_beside_it()
    {
        await using BunitContext context = CreateContext(signedIn: true);
        var popovers = context.Render<MudBlazor.MudPopoverProvider>();
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording(RecordingState.Recording));

        Assert.Empty(cut.FindAll("[data-details-meta] [data-status]"));
        IElement chip = cut.Find("[data-details-actions] [data-status]");
        Assert.Equal("rec", chip.GetAttribute("data-status"));
        Assert.Null(chip.ParentElement!.GetAttribute("role"));
        Assert.Empty(cut.FindAll("button[data-copy-playlist]"));
        Assert.Empty(cut.FindAll("button[data-export]"));

        IElement more = cut.Find("button[aria-label='More actions']");
        Assert.True(chip.CompareDocumentPosition(more).HasFlag(DocumentPositions.Following));

        more.Click();

        popovers.WaitForAssertion(() => Assert.Equal(4, popovers.FindAll("[role='menuitem']").Count));
        IReadOnlyList<IElement> items = popovers.FindAll("[role='menuitem']");
        Assert.StartsWith("Copy playlist URL", items[0].TextContent.Trim(), StringComparison.Ordinal);
        Assert.StartsWith("Export MP4", items[1].TextContent.Trim(), StringComparison.Ordinal);
        Assert.StartsWith("Stop recording", items[2].TextContent.Trim(), StringComparison.Ordinal);
        Assert.StartsWith("Delete recording", items[3].TextContent.Trim(), StringComparison.Ordinal);
        Assert.Equal("false", items[0].GetAttribute("aria-disabled"));
        Assert.Equal("true", items[1].GetAttribute("aria-disabled"));
        Assert.Contains("finishes", items[1].TextContent);
        Assert.Equal("true", items[3].GetAttribute("aria-disabled"));
    }

    [Fact]
    public async Task Signed_out_more_actions_opens_with_copy_enabled_and_admin_items_disabled()
    {
        await using BunitContext context = CreateContext(signedIn: false);
        var popovers = context.Render<MudBlazor.MudPopoverProvider>();
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording(RecordingState.Reconnecting));

        cut.Find("button[aria-label='More actions']").Click();

        popovers.WaitForAssertion(() => Assert.Equal(4, popovers.FindAll("[role='menuitem']").Count));
        IElement copy = MenuItem(popovers, "Copy playlist URL");
        Assert.Equal("false", copy.GetAttribute("aria-disabled"));
        Assert.Null(copy.GetAttribute("title"));

        foreach (string label in new[] { "Export MP4", "Stop recording", "Delete recording" })
        {
            IElement item = MenuItem(popovers, label);
            Assert.Equal("true", item.GetAttribute("aria-disabled"));
            IElement wrapper = item.ParentElement!;
            Assert.Equal("group", wrapper.GetAttribute("role"));
            Assert.Equal("0", wrapper.GetAttribute("tabindex"));
            Assert.Equal(
                "Connect Twitch to use this",
                wrapper.QuerySelector("[role='tooltip']")!.TextContent);
        }

        Assert.Equal(
            3,
            popovers.FindComponents<MudBlazor.MudTooltip>().Count(tooltip =>
                tooltip.Instance.Text == "Connect Twitch to use this" &&
                tooltip.Instance.RootClass == "sk-menu-tooltip-root"));
        Assert.Empty(cut.FindAll("[data-requires-twitch] [inert] button[aria-label='More actions']"));
    }

    [Fact]
    public async Task Signing_out_after_render_disables_the_admin_items()
    {
        await using BunitContext context = CreateContext(signedIn: true, out BunitAuthorizationContext auth);
        var popovers = context.Render<MudBlazor.MudPopoverProvider>();
        IRenderedComponent<RecordingDetails> cut = Render(context, Recording());

        IElement before = MoreActions.Item(context, cut, "data-export", popovers);
        Assert.Equal("false", before.GetAttribute("aria-disabled"));

        auth.SetNotAuthorized();

        cut.WaitForAssertion(() =>
        {
            IElement after = MoreActions.Item(context, cut, "data-export", popovers);
            Assert.Equal("true", after.GetAttribute("aria-disabled"));
            Assert.Equal(
                "Connect Twitch to use this",
                after.ParentElement!.QuerySelector("[role='tooltip']")!.TextContent);
        });
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

    private static BunitContext CreateContext(bool signedIn) => CreateContext(signedIn, out _);

    private static BunitContext CreateContext(bool signedIn, out BunitAuthorizationContext auth)
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
        auth = context.AddAuthorization();
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
