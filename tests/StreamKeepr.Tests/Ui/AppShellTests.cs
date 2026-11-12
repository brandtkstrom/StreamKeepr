using Bunit;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Components.Layout;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class AppShellTests
{
    private const string DefaultCookie =
        """{"v":1,"drawer":"expanded","theater":false,"stats":false,"details":true,"collapsed":[]}""";

    [Fact]
    public void Renders_the_full_shell_and_slots_in_order()
    {
        using var context = CreateContext();
        var cut = RenderShell(context, parameters => parameters
            .AddChildContent("<div data-test='main'>Main column</div>")
            .Add(component => component.RecordingActive, true)
            .Add(component => component.FreeBytes, 20L * 1024 * 1024 * 1024)
            .Add(component => component.TotalBytes, 100L * 1024 * 1024 * 1024));

        Assert.NotNull(cut.Find(".sk-shell"));
        Assert.NotNull(cut.Find(".sk-appbar"));
        Assert.NotNull(cut.Find(".sk-drawer"));
        Assert.NotNull(cut.Find(".sk-main .sk-main-inner [data-test='main']"));
        Assert.NotNull(cut.Find(".sk-wordmark .rec-dot"));
        Assert.NotNull(cut.Find(".sk-search input"));
        Assert.Contains("20.0 GB free", cut.Find(".sk-storage").TextContent);
        Assert.DoesNotContain("warning", cut.Find(".sk-storage").ClassList);

        var actions = cut.FindAll(".sk-appbar-actions > *");
        Assert.True(actions[^1].ClassList.Contains("sk-auth-placeholder"));
    }

    [Theory]
    [InlineData("""{"v":1,"drawer":"expanded","theater":false,"stats":false,"details":true,"collapsed":[]}""", "")]
    [InlineData("""{"v":1,"drawer":"rail","theater":false,"stats":false,"details":true,"collapsed":[]}""", "rail")]
    [InlineData("""{"v":1,"drawer":"hidden","theater":false,"stats":false,"details":true,"collapsed":[]}""", "hidden")]
    public void Renders_the_saved_drawer_mode(string cookie, string expectedClass)
    {
        using var context = CreateContext(cookie);
        var cut = RenderShell(context);

        var drawer = cut.Find(".sk-drawer");
        if (expectedClass.Length == 0)
            Assert.False(drawer.ClassList.Contains("rail"));
        else
            Assert.True(drawer.ClassList.Contains(expectedClass));
    }

    [Fact]
    public void Menu_toggles_expanded_and_rail_modes()
    {
        using var context = CreateContext();
        var cut = RenderShell(context);

        cut.Find("[data-drawer-toggle]").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Find(".sk-drawer").ClassList.Contains("rail"));
            Assert.Equal("false", cut.Find("[data-drawer-toggle]").GetAttribute("aria-expanded"));
        });

        cut.Find("[data-drawer-toggle]").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Find(".sk-drawer").ClassList.Contains("rail"));
            Assert.Equal("true", cut.Find("[data-drawer-toggle]").GetAttribute("aria-expanded"));
        });
    }

    [Fact]
    public void Theater_hides_the_drawer_and_disables_the_menu()
    {
        using var context = CreateContext(
            """{"v":1,"drawer":"expanded","theater":true,"stats":false,"details":true,"collapsed":[]}""");
        var cut = RenderShell(context);

        Assert.NotNull(cut.Find(".sk-shell.theater"));
        Assert.True(cut.Find("[data-drawer-toggle]").HasAttribute("disabled"));
    }

    [Fact]
    public void Storage_below_ten_percent_is_a_warning()
    {
        using var context = CreateContext();
        var cut = RenderShell(context, parameters => parameters
            .Add(component => component.FreeBytes, 5L * 1024 * 1024 * 1024)
            .Add(component => component.TotalBytes, 100L * 1024 * 1024 * 1024));

        Assert.True(cut.Find(".sk-storage").ClassList.Contains("warning"));
        Assert.Equal("Recording disk: 5.0 GB free of 100.0 GB", cut.Find(".sk-storage").GetAttribute("title"));
    }

    [Fact]
    public void Custom_auth_and_drawer_slots_replace_the_placeholders()
    {
        using var context = CreateContext();
        var cut = RenderShell(context, parameters => parameters
            .Add(component => component.AuthSlot, fragment =>
                fragment.AddMarkupContent(0, "<button id='custom-auth'>Account</button>"))
            .Add(component => component.Drawer, fragment =>
                fragment.AddMarkupContent(0, "<div id='custom-drawer'>Groups</div>")));

        Assert.NotNull(cut.Find(".sk-appbar-actions #custom-auth"));
        Assert.Empty(cut.FindAll(".sk-auth-placeholder"));
        Assert.NotNull(cut.Find(".sk-drawer #custom-drawer"));
    }

    [Fact]
    public void Schedule_button_invokes_its_callback()
    {
        using var context = CreateContext();
        var clicked = false;
        var cut = RenderShell(context, parameters => parameters
            .Add(component => component.OnSchedule,
                EventCallback.Factory.Create(this, () => clicked = true)));

        cut.Find("[aria-label='Capture schedule']").Click();

        Assert.True(clicked);
    }

    [Fact]
    public void Schedule_button_shows_the_stub_snackbar_without_a_handler()
    {
        using var context = CreateContext();
        var cut = RenderShell(context);

        cut.Find("[aria-label='Capture schedule']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Capture schedule is not available yet", cut.Markup);
            Assert.Single(cut.FindAll(".sk-snackbar"));
        });
    }

    [Fact]
    public async Task Reconnect_notice_is_hidden_when_the_template_modal_is_visible()
    {
        using var context = CreateContext();
        var cut = RenderShell(context);

        Assert.Empty(cut.FindAll("[data-reconnect-notice]"));

        await cut.InvokeAsync(() => cut.Instance.SetUpdatesPaused(true));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-reconnect-notice]")));

        await cut.InvokeAsync(() => cut.Instance.SetReconnectModalVisible(true));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-reconnect-notice]")));
    }

    [Fact]
    public async Task Disposing_after_the_circuit_disconnects_does_not_throw()
    {
        using var context = CreateContext();
        BunitJSModuleInterop module = context.JSInterop.SetupModule("./Components/Layout/AppShell.razor.js");
        module.Mode = JSRuntimeMode.Loose;
        module.SetupVoid("dispose", _ => true)
            .SetException(new JSDisconnectedException("The circuit is gone."));
        var cut = RenderShell(context);

        Exception? error = await Record.ExceptionAsync(
            () => cut.InvokeAsync(async () => await cut.Instance.DisposeAsync()));

        Assert.Null(error);
    }

    private static IRenderedComponent<AppShell> RenderShell(
        BunitContext context,
        Action<ComponentParameterCollectionBuilder<AppShell>>? configure = null) =>
        context.Render<AppShell>(parameters =>
        {
            parameters.AddChildContent("<div>Content</div>");
            configure?.Invoke(parameters);
        });

    private static BunitContext CreateContext(string? cookie = DefaultCookie)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();

        var store = new TestClientStateStore();
        if (cookie is not null)
            store.Cookies[UiPreferences.CookieName] = cookie;

        context.Services.AddSingleton<IClientStateStore>(store);
        context.Services.AddScoped<UiPreferences>();
        context.Services.AddScoped<ViewerClock>();
        context.Services.AddScoped<SnackbarService>();
        context.Services.AddSingleton(TimeProvider.System);
        return context;
    }
}
