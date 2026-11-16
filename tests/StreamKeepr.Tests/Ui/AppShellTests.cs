using Bunit;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Services;
using StreamKeepr.Configuration;
using StreamKeepr.Components.Layout;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class AppShellTests
{
    private const string DefaultCookie =
        """{"v":1,"drawer":"expanded","theater":false,"stats":false,"details":true,"collapsed":[]}""";

    [Fact]
    public async Task Renders_the_full_shell_and_slots_in_order()
    {
        await using var context = CreateContext();
        var cut = RenderShell(context, parameters => parameters
            .AddChildContent("<div data-test='main'>Main column</div>")
            .Add(component => component.RecordingActive, true));

        Assert.NotNull(cut.Find("[data-shell]"));
        Assert.NotNull(cut.Find("header"));
        Assert.NotNull(cut.Find("nav[aria-label='Library']"));
        Assert.NotNull(cut.Find("main [data-test='main']"));
        var wordmark = cut.Find("header a[href='/']");
        Assert.Equal("/", wordmark.GetAttribute("href"));
        Assert.Equal("StreamKeepr home", wordmark.GetAttribute("aria-label"));
        Assert.NotNull(wordmark.QuerySelector("[data-rec-dot]"));
        Assert.NotNull(cut.Find("header input[aria-label='Search recordings']"));
        // Disk usage moved to the library drawer's footer (#176).
        Assert.Empty(cut.FindAll("header [data-storage]"));

        var placeholder = cut.Find("header [data-auth-placeholder]");
        Assert.True(placeholder.NextElementSibling is null, "the auth control is the last app bar action");
    }

    [Theory]
    [InlineData("""{"v":1,"drawer":"expanded","theater":false,"stats":false,"details":true,"collapsed":[]}""", "expanded")]
    [InlineData("""{"v":1,"drawer":"rail","theater":false,"stats":false,"details":true,"collapsed":[]}""", "rail")]
    [InlineData("""{"v":1,"drawer":"hidden","theater":false,"stats":false,"details":true,"collapsed":[]}""", "hidden")]
    public async Task Renders_the_saved_drawer_mode(string cookie, string expectedMode)
    {
        await using var context = CreateContext(cookie);
        var cut = RenderShell(context);

        Assert.Equal(expectedMode, cut.Find("nav[aria-label='Library']").GetAttribute("data-drawer"));
    }

    [Fact]
    public async Task Menu_toggles_expanded_and_rail_modes()
    {
        await using var context = CreateContext();
        var cut = RenderShell(context);

        cut.Find("[data-drawer-toggle]").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("rail", cut.Find("nav[aria-label='Library']").GetAttribute("data-drawer"));
            Assert.Equal("false", cut.Find("[data-drawer-toggle]").GetAttribute("aria-expanded"));
        });

        cut.Find("[data-drawer-toggle]").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("expanded", cut.Find("nav[aria-label='Library']").GetAttribute("data-drawer"));
            Assert.Equal("true", cut.Find("[data-drawer-toggle]").GetAttribute("aria-expanded"));
        });
    }

    [Fact]
    public async Task Theater_hides_the_drawer_and_disables_the_menu()
    {
        await using var context = CreateContext(
            """{"v":1,"drawer":"expanded","theater":true,"stats":false,"details":true,"collapsed":[]}""");
        var cut = RenderShell(context);

        Assert.NotNull(cut.Find("[data-shell][data-theater-mode]"));
        Assert.True(cut.Find("[data-drawer-toggle]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Custom_auth_and_drawer_slots_replace_the_placeholders()
    {
        await using var context = CreateContext();
        var cut = RenderShell(context, parameters => parameters
            .Add(component => component.AuthSlot, fragment =>
                fragment.AddMarkupContent(0, "<button id='custom-auth'>Account</button>"))
            .Add(component => component.Drawer, fragment =>
                fragment.AddMarkupContent(0, "<div id='custom-drawer'>Groups</div>")));

        Assert.NotNull(cut.Find("header #custom-auth"));
        Assert.Empty(cut.FindAll("[data-auth-placeholder]"));
        Assert.NotNull(cut.Find("nav[aria-label='Library'] #custom-drawer"));
    }

    [Fact]
    public async Task Reconnect_notice_is_hidden_when_the_template_modal_is_visible()
    {
        await using var context = CreateContext();
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
        await using var context = CreateContext();
        BunitJSModuleInterop module = context.JSInterop.SetupModule("./Components/Layout/AppShell.razor.js");
        module.Mode = JSRuntimeMode.Loose;
        module.SetupVoid("dispose", _ => true)
            .SetException(new JSDisconnectedException("The circuit is gone."));
        var cut = RenderShell(context);

        Exception? error = await Record.ExceptionAsync(
            () => cut.InvokeAsync(async () => await cut.Instance.DisposeAsync()));

        Assert.Null(error);
    }

    [Fact]
    public async Task The_capture_schedule_placeholder_is_gone()
    {
        await using var context = CreateContext();
        var cut = RenderShell(context);

        Assert.Empty(cut.FindAll("[aria-label='Capture schedule']"));
    }

    [Fact]
    public async Task The_logo_mark_leads_the_wordmark_and_carries_the_rec_badge_only_while_recording()
    {
        await using var context = CreateContext();
        var idle = RenderShell(context);

        var wordmark = idle.Find("header a[aria-label='StreamKeepr home']");
        var mark = wordmark.QuerySelector("img");
        Assert.NotNull(mark);
        Assert.Equal("", mark!.GetAttribute("alt"));
        Assert.EndsWith("streamkeepr-mark.svg", mark.GetAttribute("src"));
        Assert.Null(wordmark.QuerySelector("[data-rec-dot]"));

        idle.Render(parameters => parameters.Add(component => component.RecordingActive, true));
        Assert.NotNull(idle.Find("header a[aria-label='StreamKeepr home'] [data-rec-dot]"));
    }

    [Fact]
    public async Task GitHub_and_Twitch_open_their_configured_urls_in_a_new_tab()
    {
        await using var context = CreateContext();
        var cut = RenderShell(context);

        var github = cut.Find("header a[aria-label='StreamKeepr on GitHub (opens in a new tab)']");
        Assert.Equal("https://github.com/example/streamkeepr", github.GetAttribute("href"));
        Assert.Equal("_blank", github.GetAttribute("target"));
        Assert.Contains("noopener", github.GetAttribute("rel"));

        var twitch = cut.Find("header a[aria-label='Twitch (opens in a new tab)']");
        Assert.Equal("https://twitch.example/", twitch.GetAttribute("href"));
        Assert.Equal("_blank", twitch.GetAttribute("target"));
        Assert.Contains("noopener", twitch.GetAttribute("rel"));
    }

    [Fact]
    public async Task Settings_is_a_real_link_in_the_app_bar()
    {
        await using var context = CreateContext();
        var cut = RenderShell(context);

        Assert.Equal("/settings", cut.Find("header a[aria-label='Settings']").GetAttribute("href"));
    }

    [Fact]
    public async Task A_watch_page_shows_the_rail_by_default_and_the_menu_keeps_its_own_choice()
    {
        await using var context = CreateContext();
        // Home starts on the rail, so a toggle that wrote both choices would show.
        UiPreferences preferences = context.Services.GetRequiredService<UiPreferences>();
        preferences.SetDrawer(DrawerMode.Rail, watching: false);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/r/abc");
        var cut = RenderShell(context);

        Assert.Equal("rail", cut.Find("nav[aria-label='Library']").GetAttribute("data-drawer"));
        Assert.NotNull(cut.Find("[data-shell][data-watching]"));

        cut.Find("[data-drawer-toggle]").Click();

        cut.WaitForAssertion(() =>
            Assert.Equal("expanded", cut.Find("nav[aria-label='Library']").GetAttribute("data-drawer")));
        Assert.Equal(DrawerMode.Expanded, preferences.WatchDrawer);
        Assert.Equal(DrawerMode.Rail, preferences.Drawer);
    }

    [Fact]
    public async Task Returning_home_restores_the_home_drawer_choice()
    {
        await using var context = CreateContext();
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/r/abc");
        var cut = RenderShell(context);
        Assert.Equal("rail", cut.Find("nav[aria-label='Library']").GetAttribute("data-drawer"));

        navigation.NavigateTo("/");

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("expanded", cut.Find("nav[aria-label='Library']").GetAttribute("data-drawer"));
            Assert.Empty(cut.FindAll("[data-shell][data-watching]"));
        });
    }

    [Fact]
    public async Task On_a_phone_the_bottom_bar_links_home_and_settings_and_marks_the_current_page()
    {
        await using var context = CreateContext();
        var cut = RenderShell(context);
        await SetBreakpointAsync(cut, Breakpoint.Xs);

        var bar = cut.Find("nav[aria-label='Main']");
        var home = bar.QuerySelector("a[href='/']")!;
        Assert.Contains("Home", home.TextContent);
        Assert.Equal("page", home.GetAttribute("aria-current"));
        var settings = bar.QuerySelector("a[href='/settings']")!;
        Assert.Contains("Settings", settings.TextContent);
        Assert.Null(settings.GetAttribute("aria-current"));
        Assert.Equal("compact", cut.Find("[data-shell]").GetAttribute("data-layout"));
    }

    [Fact]
    public async Task On_a_phone_library_opens_the_temporary_drawer_and_navigation_closes_it()
    {
        await using var context = CreateContext();
        var cut = RenderShell(context);
        await SetBreakpointAsync(cut, Breakpoint.Xs);

        var library = cut.Find("nav[aria-label='Main'] button[aria-controls='sk-drawer']");
        Assert.Contains("Library", library.TextContent);
        Assert.Equal("false", library.GetAttribute("aria-expanded"));

        library.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("[data-shell][data-drawer-modal-open]"));
            var open = cut.Find("nav[aria-label='Main'] button[aria-controls='sk-drawer']");
            Assert.Equal("true", open.GetAttribute("aria-expanded"));
            Assert.Equal("page", open.GetAttribute("aria-current"));
            Assert.Null(cut.Find("nav[aria-label='Main'] a[href='/']").GetAttribute("aria-current"));
        });

        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/settings");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-shell][data-drawer-modal-open]")));
    }

    [Fact]
    public async Task The_bottom_bar_is_desktop_free_and_hidden_on_watch_pages()
    {
        await using var context = CreateContext();
        var cut = RenderShell(context);
        await SetBreakpointAsync(cut, Breakpoint.Lg);

        Assert.Empty(cut.FindAll("nav[aria-label='Main']"));
        Assert.Equal("wide", cut.Find("[data-shell]").GetAttribute("data-layout"));

        await SetBreakpointAsync(cut, Breakpoint.Sm);
        Assert.NotEmpty(cut.FindAll("nav[aria-label='Main']"));

        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/r/abc");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("nav[aria-label='Main']")));
    }

    [Fact]
    public async Task On_a_phone_watch_page_a_back_arrow_and_the_title_replace_the_wordmark()
    {
        await using var context = CreateContext();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/r/abc");
        var cut = RenderShell(context, parameters => parameters
            .Add(component => component.WatchTitle, "Ranked climb to Diamond"));
        await SetBreakpointAsync(cut, Breakpoint.Xs);

        Assert.Equal("/", cut.Find("header a[aria-label='Back to Home']").GetAttribute("href"));
        Assert.Equal("Ranked climb to Diamond", cut.Find("header [data-watch-title]").TextContent.Trim());
        Assert.Empty(cut.FindAll("header a[aria-label='StreamKeepr home']"));
    }

    [Fact]
    public async Task On_a_phone_the_search_icon_opens_the_search_row()
    {
        await using var context = CreateContext();
        var cut = RenderShell(context);
        await SetBreakpointAsync(cut, Breakpoint.Xs);

        var toggle = cut.Find("header button[aria-label='Search']");
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll("[data-search-open]"));

        toggle.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("header [data-search-open] input[aria-label='Search recordings']"));
            Assert.Equal("true", cut.Find("header button[aria-label='Search']").GetAttribute("aria-expanded"));
        });
    }

    private static Task SetBreakpointAsync(IRenderedComponent<AppShell> cut, Breakpoint breakpoint) =>
        cut.InvokeAsync(() => ((TestBrowserViewport)cut.Services.GetRequiredService<IBrowserViewportService>())
            .SetBreakpointAsync(breakpoint));

    private static IRenderedComponent<AppShell> RenderShell(
        BunitContext context,
        Action<ComponentParameterCollectionBuilder<AppShell>>? configure = null)
    {
        // Routes.razor renders the provider once; the app bar's tooltips need it.
        context.Render<MudPopoverProvider>();
        return context.Render<AppShell>(parameters =>
        {
            parameters.AddChildContent("<div>Content</div>");
            configure?.Invoke(parameters);
        });
    }

    private static BunitContext CreateContext(string? cookie = DefaultCookie)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();
        context.Services.AddStreamKeeprMudServices();
        context.Services.AddSingleton<IBrowserViewportService>(new TestBrowserViewport());
        context.Services.AddSingleton(Options.Create(new LinksOptions
        {
            GitHub = "https://github.com/example/streamkeepr",
            Twitch = "https://twitch.example/",
        }));

        var store = new TestClientStateStore();
        if (cookie is not null)
            store.Cookies[UiPreferences.CookieName] = cookie;

        context.Services.AddSingleton<IClientStateStore>(store);
        context.Services.AddScoped<UiPreferences>();
        context.Services.AddScoped<ViewerClock>();
        context.Services.AddSingleton(TimeProvider.System);
        return context;
    }
}
