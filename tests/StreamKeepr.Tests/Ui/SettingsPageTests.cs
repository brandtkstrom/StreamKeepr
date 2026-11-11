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

namespace StreamKeepr.Tests.Ui;

public sealed class SettingsPageTests
{
    [Fact]
    public void Signed_out_shows_the_connect_prompt_and_never_creates_the_channels_view()
    {
        using BunitContext context = CreateContext(out FakeChannelsView channels, signedIn: false);

        IRenderedComponent<Settings> cut = context.Render<Settings>();

        Assert.Contains("Connect Twitch to manage channels", cut.Find("[data-settings-signed-out]").TextContent);
        Assert.Equal(
            "/api/auth/twitch/login?returnUrl=%2Fsettings",
            cut.Find("[data-settings-signed-out] a.sk-twitch").GetAttribute("href"));
        Assert.Empty(cut.FindAll("[data-channel-list]"));
        Assert.Equal(0, channels.AllReads);
    }

    [Fact]
    public void Signed_in_shows_the_account_and_channels_with_their_states()
    {
        using BunitContext context = CreateContext(out FakeChannelsView channels, signedIn: true);
        channels.Items =
        [
            new("ann", "Ann", null, true, false, ChannelRuntimeState.Recording),
            new("bob", "Bob", null, true, true, ChannelRuntimeState.Paused),
            new("cat", "Cat", null, true, false, ChannelRuntimeState.CoolingDown),
        ];

        IRenderedComponent<Settings> cut = context.Render<Settings>();

        Assert.Equal("Mock Admin", cut.Find("#twitch [data-account-name]").TextContent);
        Assert.Equal("mock_admin", cut.Find("#twitch [data-account-login]").TextContent);
        Assert.Equal("/api/auth/logout", cut.Find("#twitch form").GetAttribute("action"));
        Assert.Equal(["ann", "bob", "cat"], cut.FindAll("[data-channel]").Select(r => r.GetAttribute("data-channel")));
        Assert.Equal(
            ["Rec", "Paused", "Cooling down"],
            cut.FindAll("[data-channel-state]").Select(chip =>
                chip.TextContent.Replace(chip.QuerySelector(".sk-icon")?.TextContent ?? "\u0000", "").Trim()));
        Assert.Equal("pause", cut.Find("[data-channel='bob'] .sk-icon").TextContent);
    }

    [Theory]
    [InlineData("http://media.lan/settings", true)]
    [InlineData("http://localhost:8080/settings", false)]
    [InlineData("http://127.0.0.1/settings", false)]
    [InlineData("https://media.example/settings", false)]
    public void The_https_note_shows_only_on_plain_http_outside_localhost(string uri, bool shown)
    {
        using BunitContext context = CreateContext(out _, signedIn: false, baseUri: uri);

        IRenderedComponent<Settings> cut = context.Render<Settings>();

        Assert.Equal(shown, cut.FindAll("[data-https-note]").Count == 1);
    }

    [Fact]
    public void Add_trims_and_lowercases_shows_busy_then_clears_and_announces()
    {
        using BunitContext context = CreateContext(out FakeChannelsView channels, signedIn: true);
        var pending = new TaskCompletionSource();
        channels.OnAdd = _ => pending.Task;
        IRenderedComponent<ChannelSettings> cut = context.Render<ChannelSettings>();

        Assert.True(cut.Find("[data-channel-add]").HasAttribute("disabled"));
        cut.Find("[data-channel-login]").Input("  Mock_Wren ");
        cut.Find("[data-channel-add]").Click();

        Assert.Equal(["add:mock_wren"], channels.Calls);
        Assert.Equal("true", cut.Find("[data-channel-add]").GetAttribute("aria-busy"));
        Assert.True(cut.Find("[data-channel-add]").HasAttribute("disabled"));

        cut.InvokeAsync(pending.SetResult);

        cut.WaitForAssertion(() => Assert.Equal("", cut.Find("[data-channel-login]").GetAttribute("value")));
        Assert.Null(cut.Find("[data-channel-add]").GetAttribute("aria-busy"));
        Assert.Equal("Channel added", context.Services.GetRequiredService<SnackbarService>().Current?.Text);
    }

    [Theory]
    [InlineData("unknown", "No Twitch channel is named “mock_wren”.")]
    [InlineData("invalid", "That is not a valid Twitch login.")]
    [InlineData("duplicate", "Channel 'mock_wren' is already watched.")]
    [InlineData("unexpected", "Could not reach Twitch. Try again.")]
    [InlineData("network", "Could not reach Twitch. Try again.")]
    [InlineData("session", "Your Twitch session ended. Reconnect to manage channels.")]
    public void Add_failures_show_an_inline_error_and_keep_the_input(string failure, string message)
    {
        using BunitContext context = CreateContext(out FakeChannelsView channels, signedIn: true);
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
    public void Remove_confirms_that_recordings_are_kept_then_shows_removing_until_done()
    {
        using BunitContext context = CreateContext(out FakeChannelsView channels, signedIn: true);
        channels.Items = [new("ann", "Ann", null, true, false, ChannelRuntimeState.Recording)];
        var pending = new TaskCompletionSource();
        channels.OnRemove = _ => pending.Task;
        IRenderedComponent<ChannelSettings> cut = context.Render<ChannelSettings>();

        cut.Find("[data-channel-remove]").Click();
        Assert.Contains("Remove Ann?", cut.Find("[data-channel-remove-dialog]").TextContent);
        Assert.Contains("Recordings are kept.", cut.Find("[data-channel-remove-dialog]").TextContent);
        Assert.Empty(channels.Calls);

        cut.Find("[data-channel-remove-confirm]").Click();

        Assert.Equal(["remove:ann"], channels.Calls);
        Assert.Equal("Removing…", cut.Find("[data-channel='ann'] [role='status']").TextContent);
        Assert.Empty(cut.FindAll("[data-channel='ann'] [data-channel-remove]"));

        channels.Items = [];
        cut.InvokeAsync(pending.SetResult);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-channels-empty]")));
        Assert.Equal("Channel removed", context.Services.GetRequiredService<SnackbarService>().Current?.Text);
    }

    [Fact]
    public void A_failed_remove_restores_the_row_with_an_error()
    {
        using BunitContext context = CreateContext(out FakeChannelsView channels, signedIn: true);
        channels.Items = [new("ann", "Ann", null, true, false, ChannelRuntimeState.Watching)];
        channels.OnRemove = _ => Task.FromException(new IOException("disk"));
        IRenderedComponent<ChannelSettings> cut = context.Render<ChannelSettings>();

        cut.Find("[data-channel-remove]").Click();
        cut.Find("[data-channel-remove-confirm]").Click();

        cut.WaitForAssertion(() =>
            Assert.Equal("Could not remove Ann. Try again.", cut.Find("[data-channel='ann'] [role='alert']").TextContent));
        Assert.NotNull(cut.Find("[data-channel='ann'] [data-channel-remove]"));
    }

    private static BunitContext CreateContext(
        out FakeChannelsView channels, bool signedIn, string? baseUri = null)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();
        context.Services.AddSingleton(new AdminSessionState());
        context.Services.AddSingleton(new SnackbarService(new FakeTimeProvider()));
        context.Services.AddSingleton<AntiforgeryStateProvider>(new FixedAntiforgery());
        channels = new FakeChannelsView();
        context.Services.AddSingleton<IChannelsView>(channels);

        BunitAuthorizationContext auth = context.AddAuthorization();
        if (signedIn)
            auth.SetAuthorized("mock_admin").SetClaims(new Claim(AdminAuth.DisplayNameClaim, "Mock Admin"));
        else
            auth.SetNotAuthorized();

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(baseUri ?? "/settings");
        return context;
    }

    internal sealed class FakeChannelsView : IChannelsView
    {
        public IReadOnlyList<ChannelView> Items { get; set; } = [];

        public int AllReads { get; private set; }

        public List<string> Calls { get; } = [];

        public Func<string, Task> OnAdd { get; set; } = _ => Task.CompletedTask;

        public Func<string, Task> OnRemove { get; set; } = _ => Task.CompletedTask;

        public event Action? Changed { add { } remove { } }

        public IReadOnlyList<ChannelView> All
        {
            get
            {
                AllReads++;
                return Items;
            }
        }

        public Task AddAsync(string login, CancellationToken ct)
        {
            Calls.Add($"add:{login}");
            return OnAdd(login);
        }

        public Task RemoveAsync(string login, CancellationToken ct)
        {
            Calls.Add($"remove:{login}");
            return OnRemove(login);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FixedAntiforgery : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() =>
            new("test-token", "__RequestVerificationToken");
    }
}
