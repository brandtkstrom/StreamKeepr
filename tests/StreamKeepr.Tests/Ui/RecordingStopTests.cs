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

public sealed class RecordingStopTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_finished_recording_has_no_stop_action()
    {
        await using BunitContext context = CreateContext(out _, signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();

        var cut = Render(context, Recording(RecordingState.Saved));

        Assert.Empty(cut.FindAll("[data-stop-recording]"));
    }

    [Fact]
    public async Task A_live_recording_shows_stop_for_signed_in_admins()
    {
        await using BunitContext context = CreateContext(out TestChannelsView channels, signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();

        var cut = Render(context, Recording(RecordingState.Recording));

        Assert.NotNull(cut.Find("button[data-stop-recording]"));
        Assert.Empty(channels.Calls);
    }

    [Fact]
    public async Task Signed_out_stop_is_inert_behind_requires_twitch()
    {
        await using BunitContext context = CreateContext(out _, signedIn: false);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();

        var cut = Render(context, Recording(RecordingState.Reconnecting));

        Assert.NotNull(cut.Find("[data-requires-twitch] [inert] button[data-stop-recording]"));
    }

    [Fact]
    public async Task Stop_confirms_then_calls_the_channel_control()
    {
        await using BunitContext context = CreateContext(out TestChannelsView channels, signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        var cut = Render(context, Recording(RecordingState.Recording));

        cut.Find("button[data-stop-recording]").Click();

        Assert.True(dialogs.HasOpenDialog(StopName));
        Assert.Contains("finalized", dialogs.Find("[data-stop-dialog]").TextContent);
        Assert.Contains("stays paused", dialogs.Find("[data-stop-dialog]").TextContent);

        dialogs.Find("[data-stop-confirm]").Click();

        Assert.Equal(["stop:ann"], channels.Calls);
        Assert.False(dialogs.HasOpenDialog(StopName));
        Assert.Equal("Channel paused", context.Services.GetRequiredService<MudBlazor.ISnackbar>().CurrentMessage());
    }

    [Fact]
    public async Task A_finished_recording_closes_an_open_stop_dialog()
    {
        await using BunitContext context = CreateContext(out _, signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        var cut = Render(context, Recording(RecordingState.Recording));
        cut.Find("button[data-stop-recording]").Click();
        Assert.True(dialogs.HasOpenDialog(StopName));

        cut.Render(parameters => parameters.Add(
            component => component.Recording,
            Recording(RecordingState.Saved)));

        Assert.False(dialogs.HasOpenDialog(StopName));
    }

    [Fact]
    public async Task Unauthorized_stop_closes_with_the_reconnect_message()
    {
        await using BunitContext context = CreateContext(out TestChannelsView channels, signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        channels.OnStop = _ => Task.FromException(new UnauthorizedAccessException());
        var cut = Render(context, Recording(RecordingState.Recording));
        cut.Find("button[data-stop-recording]").Click();

        dialogs.Find("[data-stop-confirm]").Click();

        Assert.False(dialogs.HasOpenDialog(StopName));
        Assert.Contains(
            "Reconnect to manage channels",
            context.Services.GetRequiredService<MudBlazor.ISnackbar>().CurrentMessage());
    }

    private static BunitContext CreateContext(
        out TestChannelsView channels,
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
        context.Services.AddScoped<PlayerBridge>();
        context.Services.AddSingleton<IExportService>(new TestExportService());
        channels = new TestChannelsView();
        context.Services.AddSingleton<IChannelsView>(channels);
        context.Services.AddSingleton<IRecordingAdmin>(new TestRecordingAdmin());
        BunitAuthorizationContext auth = context.AddAuthorization();
        if (signedIn)
            auth.SetAuthorized("mock_admin");
        else
            auth.SetNotAuthorized();
        return context;
    }

    private const string StopName = "Stop recording for ann?";

    private static IRenderedComponent<RecordingDetails> Render(
        BunitContext context,
        RecordingView recording) =>
        context.Render<RecordingDetails>(parameters =>
            parameters.Add(component => component.Recording, recording));

    private static RecordingView Recording(RecordingState state) =>
        new(
            "r1",
            "ann",
            "Title",
            "Food",
            new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero),
            state is RecordingState.Saved or RecordingState.Partial
                ? new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero)
                : null,
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
