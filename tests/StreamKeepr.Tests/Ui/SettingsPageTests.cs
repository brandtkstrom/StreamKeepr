using MudBlazor.Services;
using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Components.Features.Settings;
using StreamKeepr.Components.Pages;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Ui;
using Microsoft.JSInterop;
using NavigationHistory = StreamKeepr.Ui.NavigationHistory;

namespace StreamKeepr.Tests.Ui;

public sealed class SettingsPageTests
{
    [Fact]
    public async Task Signed_out_shows_the_connect_prompt_and_never_creates_the_channels_view()
    {
        await using BunitContext context = CreateContext(out TestChannelsView channels, signedIn: false);

        IRenderedComponent<Settings> cut = context.Render<Settings>();

        Assert.Contains("Connect Twitch to manage channels", cut.Find("[data-settings-signed-out]").TextContent);
        Assert.Equal(
            "/api/auth/twitch/login?returnUrl=%2Fsettings",
            cut.Find("[data-settings-signed-out] a[data-twitch-connect]").GetAttribute("href"));
        Assert.Empty(cut.FindAll("[data-channel-list]"));
        Assert.Equal(0, channels.AllReads);
    }

    [Fact]
    public async Task Signed_in_shows_the_account_and_channels_with_their_states()
    {
        await using BunitContext context = CreateContext(out TestChannelsView channels, signedIn: true);
        channels.Items =
        [
            new("ann", "Ann", null, true, false, ChannelRuntimeState.Recording, true, "EventSub connected"),
            new("bob", "Bob", null, true, true, ChannelRuntimeState.Paused, true, "EventSub connected"),
            new("cat", "Cat", null, true, false, ChannelRuntimeState.CoolingDown, true, "EventSub connected"),
        ];

        IRenderedComponent<Settings> cut = context.Render<Settings>();

        Assert.Equal("Mock Admin", cut.Find("#twitch [data-account-name]").TextContent);
        Assert.Equal("mock_admin", cut.Find("#twitch [data-account-login]").TextContent);
        Assert.Equal("/api/auth/logout", cut.Find("#twitch form").GetAttribute("action"));
        Assert.Equal(["ann", "bob", "cat"], cut.FindAll("[data-channel]").Select(r => r.GetAttribute("data-channel")));
        Assert.Equal(
            ["Rec", "Paused", "Cooling down"],
            cut.FindAll("[data-channel-state]").Select(chip => chip.TextContent.Trim()));
        // StatusChip's icon is MudChip SVG now, named by a hook (#176).
        Assert.Equal("pause", cut.Find("[data-channel='bob'] [data-channel-state]").GetAttribute("data-status-icon"));
    }

    [Fact]
    public async Task The_channels_section_is_the_target_of_settings_channels()
    {
        await using BunitContext context = CreateContext(out _, signedIn: true);

        IRenderedComponent<Settings> cut = context.Render<Settings>();

        // The empty library's "Add a channel" links to /settings#channels.
        AngleSharp.Dom.IElement section = cut.Find("section#channels");
        string labelledBy = section.GetAttribute("aria-labelledby")!;
        Assert.Equal("Channels", cut.Find($"#{labelledBy}").TextContent);
    }

    [Fact]
    public async Task Connection_status_replaces_push_status_and_each_badge_is_per_channel()
    {
        await using BunitContext context = CreateContext(out TestChannelsView channels, signedIn: true);
        channels.Items =
        [
            new("ann", "Ann", null, true, false, ChannelRuntimeState.Watching, true, "EventSub connected"),
            new("bob", "Bob", null, true, false, ChannelRuntimeState.Watching, false, "User ID not resolved yet"),
        ];

        IRenderedComponent<ChannelSettings> cut = context.Render<ChannelSettings>();

        Assert.Equal("Connection status", cut.Find("[data-connection-status] h2").TextContent);
        Assert.Empty(cut.FindAll("[data-push-status]"));
        Assert.Equal("ann", cut.Find("[data-push='connected']").Closest("[data-channel]")!.GetAttribute("data-channel"));
        Assert.Equal("bob", cut.Find("[data-push='polling']").Closest("[data-channel]")!.GetAttribute("data-channel"));
        Assert.Contains("User ID not resolved yet", cut.Find("[data-push='polling']").GetAttribute("title"));
    }

    [Fact]
    public async Task Stop_confirms_finalization_then_offers_resume()
    {
        await using BunitContext context = CreateContext(out TestChannelsView channels, signedIn: true);
        channels.Items = [new("ann", "Ann", null, true, false, ChannelRuntimeState.Recording, true, "EventSub connected")];
        var pending = new TaskCompletionSource();
        channels.OnStop = _ => pending.Task;
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        IRenderedComponent<ChannelSettings> cut = context.Render<ChannelSettings>();

        cut.Find("[data-channel='ann'] [data-channel-stop]").Click();

        Assert.True(dialogs.HasOpenDialog("Stop recording for Ann?"));
        Assert.Contains("finalized", dialogs.Find("[data-channel-stop-dialog]").TextContent);
        Assert.Contains("stays paused", dialogs.Find("[data-channel-stop-dialog]").TextContent);
        Assert.Empty(channels.Calls);

        dialogs.Find("[data-channel-stop-confirm]").Click();

        Assert.Equal(["stop:ann"], channels.Calls);
        Assert.Equal("Stopping…", cut.Find("[data-channel='ann'] [role='status']").TextContent);

        channels.Items = [new("ann", "Ann", null, true, true, ChannelRuntimeState.Paused, true, "EventSub connected")];
        await cut.InvokeAsync(pending.SetResult);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-channel='ann'] [data-channel-resume]")));
        Assert.Equal("Channel paused", context.Services.GetRequiredService<MudBlazor.ISnackbar>().CurrentMessage());
    }

    [Fact]
    public async Task Resume_is_immediate_and_restores_the_stop_action()
    {
        await using BunitContext context = CreateContext(out TestChannelsView channels, signedIn: true);
        channels.Items = [new("ann", "Ann", null, true, true, ChannelRuntimeState.Paused, true, "EventSub connected")];
        var pending = new TaskCompletionSource();
        channels.OnResume = _ => pending.Task;
        IRenderedComponent<ChannelSettings> cut = context.Render<ChannelSettings>();

        cut.Find("[data-channel='ann'] [data-channel-resume]").Click();

        Assert.Equal(["resume:ann"], channels.Calls);
        Assert.Equal("Resuming…", cut.Find("[data-channel='ann'] [role='status']").TextContent);

        channels.Items = [new("ann", "Ann", null, true, false, ChannelRuntimeState.Watching, true, "EventSub connected")];
        await cut.InvokeAsync(pending.SetResult);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-channel='ann'] [data-channel-stop]")));
        Assert.Equal("Recording resumed", context.Services.GetRequiredService<MudBlazor.ISnackbar>().CurrentMessage());
    }

    [Theory]
    [InlineData("http://media.lan/settings", true)]
    [InlineData("http://localhost:8080/settings", false)]
    [InlineData("http://127.0.0.1/settings", false)]
    [InlineData("https://media.example/settings", false)]
    public async Task The_https_note_shows_only_on_plain_http_outside_localhost(string uri, bool shown)
    {
        await using BunitContext context = CreateContext(out _, signedIn: false, baseUri: uri);

        IRenderedComponent<Settings> cut = context.Render<Settings>();

        Assert.Equal(shown, cut.FindAll("[data-https-note]").Count == 1);
    }

    [Fact]
    public async Task Add_trims_and_lowercases_shows_busy_then_clears_and_announces()
    {
        await using BunitContext context = CreateContext(out TestChannelsView channels, signedIn: true);
        var pending = new TaskCompletionSource();
        channels.OnAdd = _ => pending.Task;
        IRenderedComponent<ChannelSettings> cut = context.Render<ChannelSettings>();

        Assert.True(cut.Find("[data-channel-add]").HasAttribute("disabled"));
        cut.Find("[data-channel-login]").Input("  Mock_Wren ");
        cut.Find("[data-channel-add]").Click();

        Assert.Equal(["add:mock_wren"], channels.Calls);
        Assert.Equal("true", cut.Find("[data-channel-add]").GetAttribute("aria-busy"));
        Assert.True(cut.Find("[data-channel-add]").HasAttribute("disabled"));

        await cut.InvokeAsync(pending.SetResult);

        cut.WaitForAssertion(() => Assert.Equal("", cut.Find("[data-channel-login]").GetAttribute("value")));
        Assert.Null(cut.Find("[data-channel-add]").GetAttribute("aria-busy"));
        Assert.Equal("Channel added", context.Services.GetRequiredService<MudBlazor.ISnackbar>().CurrentMessage());
    }

    [Theory]
    [InlineData("unknown", "No Twitch channel is named “mock_wren”.")]
    [InlineData("invalid", "That is not a valid Twitch login.")]
    [InlineData("duplicate", "Channel 'mock_wren' is already watched.")]
    [InlineData("unexpected", "Could not reach Twitch. Try again.")]
    [InlineData("network", "Could not reach Twitch. Try again.")]
    [InlineData("session", "Your Twitch session ended. Reconnect to manage channels.")]
    public async Task Add_failures_show_an_inline_error_and_keep_the_input(string failure, string message)
    {
        await using BunitContext context = CreateContext(out TestChannelsView channels, signedIn: true);
        channels.OnAdd = login => Task.FromException(failure switch
        {
            "unknown" => new UnknownChannelException(login),
            "invalid" => new ArgumentException("bad"),
            "duplicate" => new InvalidOperationException($"Channel '{login}' is already watched."),
            "unexpected" => new FormatException("odd"),
            "network" => new HttpRequestException("down"),
            _ => new UnauthorizedAccessException(),
        });
        IRenderedComponent<ChannelSettings> cut = context.Render<ChannelSettings>();

        cut.Find("[data-channel-login]").Input("mock_wren");
        cut.Find("[data-channel-login]").KeyDown("Enter");

        cut.WaitForAssertion(() => Assert.Equal(message, cut.Find("[data-channel-add-error]").TextContent));
        Assert.Equal("alert", cut.Find("[data-channel-add-error]").GetAttribute("role"));
        Assert.Equal("true", cut.Find("[data-channel-login]").GetAttribute("aria-invalid"));
        Assert.Equal("mock_wren", cut.Find("[data-channel-login]").GetAttribute("value"));
    }

    [Fact]
    public async Task Remove_confirms_that_recordings_are_kept_then_shows_removing_until_done()
    {
        await using BunitContext context = CreateContext(out TestChannelsView channels, signedIn: true);
        channels.Items = [new("ann", "Ann", null, true, false, ChannelRuntimeState.Recording, true, "EventSub connected")];
        var pending = new TaskCompletionSource();
        channels.OnRemove = _ => pending.Task;
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        IRenderedComponent<ChannelSettings> cut = context.Render<ChannelSettings>();

        cut.Find("[data-channel-remove]").Click();
        Assert.True(dialogs.HasOpenDialog("Remove Ann?"));
        Assert.Contains("Recordings are kept.", dialogs.Find("[data-channel-remove-dialog]").TextContent);
        Assert.Empty(channels.Calls);

        dialogs.Find("[data-channel-remove-confirm]").Click();

        Assert.Equal(["remove:ann"], channels.Calls);
        Assert.False(dialogs.HasOpenDialog("Remove Ann?"));
        Assert.Equal("Removing…", cut.Find("[data-channel='ann'] [role='status']").TextContent);
        Assert.Empty(cut.FindAll("[data-channel='ann'] [data-channel-remove]"));

        channels.Items = [];
        await cut.InvokeAsync(pending.SetResult);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-channels-empty]")));
        Assert.Equal("Channel removed", context.Services.GetRequiredService<MudBlazor.ISnackbar>().CurrentMessage());
    }

    [Fact]
    public async Task A_failed_remove_restores_the_row_with_an_error()
    {
        await using BunitContext context = CreateContext(out TestChannelsView channels, signedIn: true);
        channels.Items = [new("ann", "Ann", null, true, false, ChannelRuntimeState.Watching, true, "EventSub connected")];
        channels.OnRemove = _ => Task.FromException(new IOException("disk"));
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        IRenderedComponent<ChannelSettings> cut = context.Render<ChannelSettings>();

        cut.Find("[data-channel-remove]").Click();
        dialogs.Find("[data-channel-remove-confirm]").Click();

        cut.WaitForAssertion(() =>
            Assert.Equal("Could not remove Ann. Try again.", cut.Find("[data-channel='ann'] [role='alert']").TextContent));
        Assert.NotNull(cut.Find("[data-channel='ann'] [data-channel-remove]"));
    }

    [Fact]
    public async Task The_header_puts_an_exit_button_before_the_heading()
    {
        await using BunitContext context = CreateContext(out _, signedIn: true);

        IRenderedComponent<Settings> cut = context.Render<Settings>();

        AngleSharp.Dom.IElement exit = ExitButton(cut);
        AngleSharp.Dom.IElement heading = cut.Find("h1");
        Assert.Equal("Settings", heading.TextContent.Trim());
        Assert.Equal("Escape", exit.GetAttribute("aria-keyshortcuts"));
        // Document order: the exit comes first, then the heading.
        Assert.Equal(
            [exit, heading],
            cut.FindAll("button[aria-label='Exit settings'], h1"));
    }

    [Fact]
    public async Task Exit_goes_home_after_a_direct_visit()
    {
        await using BunitContext context = CreateContext(out _, signedIn: true);
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        IRenderedComponent<Settings> cut = context.Render<Settings>();
        ExitButton(cut).Click();

        Assert.Equal("http://localhost/", navigation.Uri);
        Assert.Empty(context.JSInterop.Invocations["history.back"]);
    }

    [Fact]
    public async Task Exit_goes_back_to_the_previous_page_in_history()
    {
        await using BunitContext context = CreateContext(out _, signedIn: true);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var history = new NavigationHistory("http://localhost/r/abc");
        history.Visit(navigation.Uri);

        IRenderedComponent<CascadingValue<NavigationHistory>> cut = context.Render<CascadingValue<NavigationHistory>>(
            parameters => parameters
                .Add(value => value.Value, history)
                .Add(value => value.IsFixed, true)
                .AddChildContent<Settings>());
        ExitButton(cut).Click();

        context.JSInterop.VerifyInvoke("history.back");
        Assert.Equal("http://localhost/settings", navigation.Uri);
    }

    [Fact]
    public async Task Escape_is_wired_to_exit_through_the_page_module()
    {
        await using BunitContext context = CreateContext(out _, signedIn: true);
        BunitJSModuleInterop module = context.JSInterop.SetupModule("./Components/Pages/Settings.razor.js");

        IRenderedComponent<Settings> cut = context.Render<Settings>();

        JSRuntimeInvocation install = module.VerifyInvoke("install");
        Assert.IsType<DotNetObjectReference<Settings>>(install.Arguments[0]);
        await cut.InvokeAsync(() => cut.Instance.ExitAsync());
        Assert.Equal("http://localhost/", context.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task Signed_in_sections_are_twitch_account_channels_then_connection_status()
    {
        await using BunitContext context = CreateContext(out _, signedIn: true);

        IRenderedComponent<Settings> cut = context.Render<Settings>();

        // The page and the settings sheet (F2) render the same shared sections.
        Assert.NotNull(cut.FindComponent<SettingsSections>());
        Assert.Equal(
            ["Twitch account", "Channels", "Connection status"],
            cut.FindAll("section[aria-labelledby]")
                .Select(section => cut.Find($"#{section.GetAttribute("aria-labelledby")}").TextContent.Trim()));
    }

    [Fact]
    public async Task The_account_avatar_takes_the_login_channel_colour()
    {
        await using BunitContext context = CreateContext(out _, signedIn: true);

        IRenderedComponent<SettingsSections> cut = context.Render<SettingsSections>();

        Assert.Equal(
            ChannelColor.For("mock_admin").ToString(System.Globalization.CultureInfo.InvariantCulture),
            cut.Find("#twitch [data-channel-color]").GetAttribute("data-channel-color"));
        Assert.Contains("Connected", cut.Find("#twitch [data-account-connected]").TextContent);
    }

    [Fact]
    public async Task Channel_rows_are_a_list_and_the_add_field_is_labelled()
    {
        await using BunitContext context = CreateContext(out TestChannelsView channels, signedIn: true);
        channels.Items =
        [
            new("ann", "Ann", null, true, false, ChannelRuntimeState.Watching, true, "EventSub connected"),
            new("bob", "Bob", null, true, false, ChannelRuntimeState.Watching, false, "User ID not resolved yet"),
        ];

        IRenderedComponent<ChannelSettings> cut = context.Render<ChannelSettings>();

        Assert.Equal("list", cut.Find("[data-channel-list]").GetAttribute("role"));
        Assert.All(cut.FindAll("[data-channel]"), row => Assert.Equal("listitem", row.GetAttribute("role")));
        string id = cut.Find("[data-channel-login]").GetAttribute("id")!;
        Assert.Equal("Add channel", cut.Find($"label[for='{id}']").TextContent.Trim());
        Assert.Equal("false", cut.Find("[data-channel-login]").GetAttribute("spellcheck"));
    }

    [Fact]
    public async Task Push_is_healthy_and_polling_is_amber_with_a_schedule_icon()
    {
        await using BunitContext context = CreateContext(out TestChannelsView channels, signedIn: true);
        channels.Items =
        [
            new("ann", "Ann", null, true, false, ChannelRuntimeState.Watching, true, "EventSub connected"),
            new("bob", "Bob", null, true, false, ChannelRuntimeState.Watching, false, "User ID not resolved yet"),
        ];

        IRenderedComponent<ChannelSettings> cut = context.Render<ChannelSettings>();

        Assert.Equal("healthy", cut.Find("[data-push='connected']").GetAttribute("data-status"));
        Assert.Equal("partial", cut.Find("[data-push='polling']").GetAttribute("data-status"));
        Assert.Equal("schedule", cut.Find("[data-push='polling']").GetAttribute("data-status-icon"));
    }

    private static AngleSharp.Dom.IElement ExitButton<TComponent>(IRenderedComponent<TComponent> cut)
        where TComponent : IComponent =>
        cut.Find("button[aria-label='Exit settings']");

    private static BunitContext CreateContext(
        out TestChannelsView channels, bool signedIn, string? baseUri = null)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddStreamKeeprMudServices();
        context.Services.AddLogging();
        context.Services.AddSingleton(new AdminSessionState());
        context.Services.AddSingleton<AntiforgeryStateProvider>(new FixedAntiforgery());
        channels = new TestChannelsView();
        context.Services.AddSingleton<IChannelsView>(channels);
        context.Services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        context.Services.AddSingleton<IConnectionStatusView>(new TestConnectionStatusView
        {
            Status = new ConnectionStatus(
                "WebSocket", EventSubConnection.Connected, 2, 2,
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy, "2 channels watched.",
                DateTimeOffset.UnixEpoch, null, DateTimeOffset.UnixEpoch, null, null, null,
                TimeSpan.FromSeconds(30)),
        });

        BunitAuthorizationContext auth = context.AddAuthorization();
        if (signedIn)
            auth.SetAuthorized("mock_admin").SetClaims(new Claim(AdminAuth.DisplayNameClaim, "Mock Admin"));
        else
            auth.SetNotAuthorized();

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(baseUri ?? "/settings");
        return context;
    }

    private sealed class FixedAntiforgery : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() =>
            new("test-token", "__RequestVerificationToken");
    }
}
