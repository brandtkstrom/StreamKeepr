using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MudBlazor;
using MudBlazor.Services;
using StreamKeepr.Components.Features.Auth;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class TwitchAccountTests
{
    [Fact]
    public async Task Disconnected_is_a_full_page_connect_link_with_the_current_return_url()
    {
        await using BunitContext context = CreateContext(out _);
        context.AddAuthorization().SetNotAuthorized();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/r/abc?q=chef&authError=failed");

        IRenderedComponent<TwitchAccount> cut = RenderAccount(context);

        var link = cut.Find("a[data-auth-state='disconnected']");
        Assert.Equal("/api/auth/twitch/login?returnUrl=%2Fr%2Fabc%3Fq%3Dchef", link.GetAttribute("href"));
        Assert.Equal("false", link.GetAttribute("data-enhance-nav"));
        Assert.Contains("Connect with Twitch", link.TextContent);
        Assert.Empty(cut.FindAll("form"));
    }

    [Fact]
    public async Task The_return_url_follows_navigation()
    {
        await using BunitContext context = CreateContext(out _);
        context.AddAuthorization().SetNotAuthorized();
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        IRenderedComponent<TwitchAccount> cut = RenderAccount(context);

        navigation.NavigateTo("/r/xyz?sort=az");

        cut.WaitForAssertion(() => Assert.Equal(
            "/api/auth/twitch/login?returnUrl=%2Fr%2Fxyz%3Fsort%3Daz",
            cut.Find("a[data-twitch-connect]").GetAttribute("href")));
    }

    [Fact]
    public async Task Connected_is_an_avatar_only_activator_named_for_the_account()
    {
        await using BunitContext context = CreateContext(out _);
        context.AddAuthorization()
            .SetAuthorized("mock_admin")
            .SetClaims(new Claim(StreamKeepr.Infrastructure.Twitch.AdminAuth.DisplayNameClaim, "Mock Admin"));

        IRenderedComponent<TwitchAccount> cut = RenderAccount(context);

        var activator = Activator(cut);
        // The name is for screen readers only: the bar shows the avatar alone.
        Assert.Equal("Twitch account: Mock Admin", activator.QuerySelector("[data-account-label]")?.TextContent);
        Assert.Equal("menu", activator.GetAttribute("aria-haspopup"));
        Assert.NotNull(activator.QuerySelector("[data-account-avatar]"));
        Assert.NotNull(activator.QuerySelector("[data-auth-dot]"));
        Assert.Empty(cut.FindAll("[data-account-login]"));
        Assert.Empty(cut.FindAll("[role='menuitem']"));
    }

    [Fact]
    public async Task The_account_menu_shows_who_is_signed_in_and_links_manage_and_settings()
    {
        await using BunitContext context = CreateContext(out _);
        context.AddAuthorization()
            .SetAuthorized("mock_admin")
            .SetClaims(new Claim(StreamKeepr.Infrastructure.Twitch.AdminAuth.DisplayNameClaim, "Mock Admin"));
        IRenderedComponent<TwitchAccount> cut = RenderAccount(context, out var popovers);

        Activator(cut).QuerySelector("[data-account-trigger]")!.Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(Menu(popovers).QuerySelectorAll("[role='menuitem']")));
        var menu = Menu(popovers);
        var header = menu.QuerySelector("[data-account-header]")!;
        Assert.Contains("Mock Admin", header.TextContent);
        Assert.Equal("mock_admin", header.QuerySelector("[data-account-login]")?.TextContent);
        Assert.Contains("Connected to Twitch", header.TextContent);
        Assert.NotNull(header.QuerySelector("[data-auth-dot]"));

        var items = menu.QuerySelectorAll("[role='menuitem']");
        Assert.Equal(
            ["Manage Twitch connection", "Settings", "Disconnect"],
            items.Select(item => item.TextContent.Trim()));
        Assert.Equal("/settings#twitch", items[0].GetAttribute("href"));
        Assert.Equal("/settings", items[1].GetAttribute("href"));
    }

    [Fact]
    public async Task Disconnect_submits_the_antiforgery_form_for_the_current_page()
    {
        await using BunitContext context = CreateContext(out _);
        BunitJSModuleInterop module = context.JSInterop.SetupModule("./Components/Features/Auth/TwitchAccount.razor.js");
        module.Mode = JSRuntimeMode.Loose;
        context.AddAuthorization().SetAuthorized("mock_admin");
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/r/abc?state=saved");
        IRenderedComponent<TwitchAccount> cut = RenderAccount(context, out var popovers);

        var form = cut.Find($"form#{TwitchAccount.LogoutFormId}");
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal("/api/auth/logout", form.GetAttribute("action"));
        Assert.Equal("test-token", form.QuerySelector("input[name='__RequestVerificationToken']")?.GetAttribute("value"));
        Assert.Equal("/r/abc?state=saved", form.QuerySelector("input[name='returnUrl']")?.GetAttribute("value"));

        Activator(cut).QuerySelector("[data-account-trigger]")!.Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(Menu(popovers).QuerySelectorAll("[role='menuitem']")));
        Menu(popovers).QuerySelectorAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Disconnect").Click();

        cut.WaitForAssertion(() => Assert.Contains(
            module.Invocations,
            call => call.Identifier == "submitForm" && Equals(call.Arguments[0], TwitchAccount.LogoutFormId)));
    }

    [Fact]
    public async Task A_disconnect_that_cannot_submit_says_so()
    {
        await using BunitContext context = CreateContext(out _);
        BunitJSModuleInterop module = context.JSInterop.SetupModule("./Components/Features/Auth/TwitchAccount.razor.js");
        module.Mode = JSRuntimeMode.Loose;
        module.SetupVoid("submitForm", _ => true).SetException(new Microsoft.JSInterop.JSException("form gone"));
        context.AddAuthorization().SetAuthorized("mock_admin");
        IRenderedComponent<TwitchAccount> cut = RenderAccount(context, out var popovers);

        Activator(cut).QuerySelector("[data-account-trigger]")!.Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(Menu(popovers).QuerySelectorAll("[role='menuitem']")));
        Menu(popovers).QuerySelectorAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Disconnect").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "Could not disconnect. Reload the page and try again.",
            context.Services.GetRequiredService<StreamKeepr.Ui.SnackbarService>().Current?.Text));
    }

    [Fact]
    public async Task Expired_replaces_the_chip_with_a_reconnect_link_and_keeps_admin_controls_inert()
    {
        await using BunitContext context = CreateContext(out AdminSessionState session);
        BunitAuthorizationContext authorization = context.AddAuthorization().SetAuthorized("mock_admin");
        IRenderedComponent<TwitchAccount> cut = RenderAccount(context);
        Assert.NotEmpty(cut.FindAll("[data-auth-state='connected'] [role='button'][aria-haspopup='menu']"));

        await cut.InvokeAsync(() =>
        {
            authorization.SetNotAuthorized();
            session.MarkExpired("mock_admin");
        });

        cut.WaitForAssertion(() =>
        {
            var expired = cut.Find("[data-auth-state='expired']");
            Assert.Contains("Twitch session expired", expired.TextContent);
            Assert.StartsWith("/api/auth/twitch/login?returnUrl=", expired.QuerySelector("a")?.GetAttribute("href"));
            Assert.Empty(cut.FindAll("[data-auth-state='connected'] [role='button'][aria-haspopup='menu']"));
            Assert.Empty(cut.FindAll("[data-auth-state='disconnected']"));
        });
    }

    [Fact]
    public async Task Admin_controls_and_the_top_bar_follow_the_revalidating_provider()
    {
        await using BunitContext context = CreateContext(out AdminSessionState session);
        var clock = new FakeTimeProvider();
        // The real authorization services, not bUnit's test doubles, so the provider is the
        // only source of auth state.
        context.Services.AddAuthorizationCore();
        context.Services.Replace(ServiceDescriptor.Transient<IAuthorizationService, DefaultAuthorizationService>());
        context.Services.Replace(ServiceDescriptor.Transient<IAuthorizationPolicyProvider, DefaultAuthorizationPolicyProvider>());
        context.Services.AddCascadingAuthenticationState();
        context.Services.AddSingleton<TimeProvider>(clock);
        context.Services.AddSingleton<IAdminSessionValidator>(new RejectingValidator());
        context.Services.AddScoped<AuthenticationStateProvider, AdminAuthenticationStateProvider>();
        var provider = (AdminAuthenticationStateProvider)context.Services
            .GetRequiredService<AuthenticationStateProvider>();
        provider.SetAuthenticationState(AdminAuthenticationStateProviderTests.SignedIn("mock_admin"));

        IRenderedComponent<TwitchAccount> account = RenderAccount(context);
        IRenderedComponent<RequiresTwitch> admin = context.Render<RequiresTwitch>(p =>
            p.AddChildContent("<button data-admin>Export</button>"));
        Assert.NotEmpty(account.FindAll("[data-auth-state='connected'] [role='button'][aria-haspopup='menu']"));
        Assert.Empty(admin.FindAll("[inert]"));

        clock.Advance(AdminAuthenticationStateProvider.RevalidationInterval);

        account.WaitForAssertion(() =>
            Assert.Contains("Twitch session expired", account.Find("[data-auth-state='expired']").TextContent));
        admin.WaitForAssertion(() => Assert.NotNull(admin.Find("[inert] [data-admin]")));
        Assert.True(session.Expired);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("notAllowed", "This Twitch account is not allowed to manage Stream Keepr.")]
    [InlineData("httpsRequired", "Twitch sign-in requires HTTPS except on localhost.")]
    [InlineData("failed", "Twitch sign-in failed. Try again.")]
    public async Task An_auth_error_shows_one_snackbar_and_leaves_only_the_other_keys(string code, string message)
    {
        await using BunitContext context = CreateContext(out _);
        var navigation = (BunitNavigationManager)context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"/r/abc?q=chef&authError={code}&sort=az");
        StreamKeepr.Ui.SnackbarService snackbars = context.Services.GetRequiredService<StreamKeepr.Ui.SnackbarService>();

        context.Render<AuthErrorNotifier>();

        Assert.Equal(message, snackbars.Current?.Text);
        Assert.EndsWith("/r/abc?q=chef&sort=az", navigation.Uri);
        Assert.True(navigation.History.First().Options.ReplaceHistoryEntry);
    }

    [Fact]
    public async Task No_auth_error_means_no_snackbar_and_no_navigation()
    {
        await using BunitContext context = CreateContext(out _);
        var navigation = (BunitNavigationManager)context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/r/abc?q=chef");
        int before = navigation.History.Count;

        context.Render<AuthErrorNotifier>();

        Assert.Null(context.Services.GetRequiredService<StreamKeepr.Ui.SnackbarService>().Current);
        Assert.Equal(before, navigation.History.Count);
    }

    private static IRenderedComponent<TwitchAccount> RenderAccount(BunitContext context) =>
        RenderAccount(context, out _);

    private static IRenderedComponent<TwitchAccount> RenderAccount(
        BunitContext context, out IRenderedComponent<MudPopoverProvider> popovers)
    {
        // Routes.razor renders the provider once; the account menu opens into it.
        popovers = context.Render<MudPopoverProvider>();
        return context.Render<TwitchAccount>();
    }

    private static AngleSharp.Dom.IElement Activator(IRenderedComponent<TwitchAccount> cut) =>
        cut.Find("[data-auth-state='connected'] [role='button'][aria-haspopup='menu']");

    private static AngleSharp.Dom.IElement Menu(IRenderedComponent<MudPopoverProvider> popovers) =>
        popovers.Find("[role='menu']");

    private static BunitContext CreateContext(out AdminSessionState session)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();
        context.Services.AddMudServices();
        session = new AdminSessionState();
        context.Services.AddSingleton(session);
        context.Services.AddSingleton<StreamKeepr.Ui.SnackbarService>(new StreamKeepr.Ui.SnackbarService(new FakeTimeProvider()));
        context.Services.AddSingleton<AntiforgeryStateProvider>(new FixedAntiforgery());
        return context;
    }

    private sealed class RejectingValidator : IAdminSessionValidator
    {
        public Task<bool> IsValidAsync(ClaimsPrincipal user, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class FixedAntiforgery : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() =>
            new("test-token", "__RequestVerificationToken");
    }
}
