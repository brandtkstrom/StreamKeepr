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
using StreamKeepr.Components.Features.Auth;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class TwitchAccountTests
{
    [Fact]
    public void Disconnected_is_a_full_page_connect_link_with_the_current_return_url()
    {
        using BunitContext context = CreateContext(out _);
        context.AddAuthorization().SetNotAuthorized();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/r/abc?q=chef&authError=failed");

        IRenderedComponent<TwitchAccount> cut = context.Render<TwitchAccount>();

        var link = cut.Find("a.sk-twitch[data-auth-state='disconnected']");
        Assert.Equal("/api/auth/twitch/login?returnUrl=%2Fr%2Fabc%3Fq%3Dchef", link.GetAttribute("href"));
        Assert.Equal("false", link.GetAttribute("data-enhance-nav"));
        Assert.Contains("Connect with Twitch", link.TextContent);
        Assert.Empty(cut.FindAll("form"));
    }

    [Fact]
    public void The_return_url_follows_navigation()
    {
        using BunitContext context = CreateContext(out _);
        context.AddAuthorization().SetNotAuthorized();
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        IRenderedComponent<TwitchAccount> cut = context.Render<TwitchAccount>();

        navigation.NavigateTo("/r/xyz?sort=az");

        cut.WaitForAssertion(() => Assert.Equal(
            "/api/auth/twitch/login?returnUrl=%2Fr%2Fxyz%3Fsort%3Daz",
            cut.Find("a.sk-twitch").GetAttribute("href")));
    }

    [Fact]
    public void Connected_shows_the_account_chip_with_manage_and_an_antiforgery_disconnect_form()
    {
        using BunitContext context = CreateContext(out _);
        context.AddAuthorization()
            .SetAuthorized("mock_admin")
            .SetClaims(new Claim(StreamKeepr.Infrastructure.Twitch.AdminAuth.DisplayNameClaim, "Mock Admin"));
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/r/abc?state=saved");

        IRenderedComponent<TwitchAccount> cut = context.Render<TwitchAccount>();

        var chip = cut.Find("[data-auth-state='connected'] button.sk-auth-chip");
        // Named by its visible text plus a visually hidden prefix (label in name).
        Assert.Null(chip.GetAttribute("aria-label"));
        Assert.Equal("Twitch account: Mock Admin", chip.QuerySelector(".lbl")?.TextContent);
        Assert.Contains("Mock Admin", chip.TextContent);
        Assert.NotNull(cut.Find(".sk-auth-chip .sk-auth-dot"));
        Assert.Equal("mock_admin", cut.Find(".sk-auth-chip .login").TextContent);

        var items = cut.FindAll("[role='menuitem']");
        Assert.Equal(["Manage Twitch connection", "Disconnect"], items.Select(i => i.TextContent.Trim().Replace("manage_accounts", "").Replace("logout", "").Trim()));

        var disconnect = items[1];
        Assert.Equal("submit", disconnect.GetAttribute("type"));
        Assert.Equal(TwitchAccount.LogoutFormId, disconnect.GetAttribute("form"));

        var form = cut.Find($"form#{TwitchAccount.LogoutFormId}");
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal("/api/auth/logout", form.GetAttribute("action"));
        Assert.Equal("test-token", form.QuerySelector("input[name='__RequestVerificationToken']")?.GetAttribute("value"));
        Assert.Equal("/r/abc?state=saved", form.QuerySelector("input[name='returnUrl']")?.GetAttribute("value"));

        chip.Click();
        items[0].Click();
        Assert.EndsWith("/settings#twitch", navigation.Uri);
    }

    [Fact]
    public void Expired_replaces_the_chip_with_a_reconnect_link_and_keeps_admin_controls_inert()
    {
        using BunitContext context = CreateContext(out AdminSessionState session);
        BunitAuthorizationContext authorization = context.AddAuthorization().SetAuthorized("mock_admin");
        IRenderedComponent<TwitchAccount> cut = context.Render<TwitchAccount>();
        Assert.NotEmpty(cut.FindAll(".sk-auth-chip"));

        cut.InvokeAsync(() =>
        {
            authorization.SetNotAuthorized();
            session.MarkExpired("mock_admin");
        });

        cut.WaitForAssertion(() =>
        {
            var expired = cut.Find("[data-auth-state='expired']");
            Assert.Contains("Twitch session expired", expired.TextContent);
            Assert.StartsWith("/api/auth/twitch/login?returnUrl=", expired.QuerySelector("a")?.GetAttribute("href"));
            Assert.Empty(cut.FindAll(".sk-auth-chip"));
            Assert.Empty(cut.FindAll("[data-auth-state='disconnected']"));
        });
    }

    [Fact]
    public async Task Admin_controls_and_the_top_bar_follow_the_revalidating_provider()
    {
        using BunitContext context = CreateContext(out AdminSessionState session);
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

        IRenderedComponent<TwitchAccount> account = context.Render<TwitchAccount>();
        IRenderedComponent<RequiresTwitch> admin = context.Render<RequiresTwitch>(p =>
            p.AddChildContent("<button data-admin>Export</button>"));
        Assert.NotEmpty(account.FindAll(".sk-auth-chip"));
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
    public void An_auth_error_shows_one_snackbar_and_leaves_only_the_other_keys(string code, string message)
    {
        using BunitContext context = CreateContext(out _);
        var navigation = (BunitNavigationManager)context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"/r/abc?q=chef&authError={code}&sort=az");
        SnackbarService snackbars = context.Services.GetRequiredService<SnackbarService>();

        context.Render<AuthErrorNotifier>();

        Assert.Equal(message, snackbars.Current?.Text);
        Assert.EndsWith("/r/abc?q=chef&sort=az", navigation.Uri);
        Assert.True(navigation.History.First().Options.ReplaceHistoryEntry);
    }

    [Fact]
    public void No_auth_error_means_no_snackbar_and_no_navigation()
    {
        using BunitContext context = CreateContext(out _);
        var navigation = (BunitNavigationManager)context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/r/abc?q=chef");
        int before = navigation.History.Count;

        context.Render<AuthErrorNotifier>();

        Assert.Null(context.Services.GetRequiredService<SnackbarService>().Current);
        Assert.Equal(before, navigation.History.Count);
    }

    private static BunitContext CreateContext(out AdminSessionState session)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();
        session = new AdminSessionState();
        context.Services.AddSingleton(session);
        context.Services.AddSingleton<SnackbarService>(new SnackbarService(new FakeTimeProvider()));
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
