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
        // An SVG icon, no ligature text beside the label (#181).
        Assert.Equal("Connect with Twitch", placeholder.TextContent.Trim());
        Assert.NotNull(placeholder.QuerySelector("svg[data-twitch-icon]"));
    }

    [Theory]
    [InlineData("""{"v":1,"drawer":"expanded","theater":false,"stats":false,"details":true,"collapsed":[]}""")]
    [InlineData("""{"v":1,"drawer":"rail","theater":false,"stats":false,"details":true,"collapsed":[]}""")]
    [InlineData("""{"v":1,"drawer":"hidden","theater":false,"stats":false,"details":true,"collapsed":[]}""")]
    public async Task Md_and_up_the_library_starts_as_the_rail_whatever_an_older_cookie_saved(string cookie)
    {
        // The open library lies over the page and is not remembered (design system: LibraryDrawer).
        await using var context = CreateContext(cookie);
        var cut = RenderShell(context);

        Assert.Equal("rail", cut.Find("nav[aria-label='Library']").GetAttribute("data-drawer"));
        Assert.Empty(cut.FindAll("[data-shell][data-library-open]"));
        Assert.Empty(cut.FindAll("[data-shell][data-drawer-frame='hidden']"));
    }

    [Fact]
    public async Task The_menu_opens_the_library_over_the_page_and_closes_it()
    {
        await using var context = CreateContext();
        var cut = RenderShell(context);
        Assert.Equal("Menu: open library", cut.Find("[data-drawer-toggle]").GetAttribute("aria-label"));

        cut.Find("[data-drawer-toggle]").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("expanded", cut.Find("nav[aria-label='Library']").GetAttribute("data-drawer"));
            Assert.Equal("true", cut.Find("[data-drawer-toggle]").GetAttribute("aria-expanded"));
            Assert.Equal("Menu: close library", cut.Find("[data-drawer-toggle]").GetAttribute("aria-label"));
            Assert.NotNull(cut.Find("[data-shell][data-library-open]"));
            // Md and up it is not modal: no backdrop, and the phone's modal hook stays off.
            Assert.Empty(cut.FindAll("[data-shell][data-drawer-modal-open]"));
        });

        cut.Find("[data-drawer-toggle]").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("rail", cut.Find("nav[aria-label='Library']").GetAttribute("data-drawer"));
            Assert.Equal("false", cut.Find("[data-drawer-toggle]").GetAttribute("aria-expanded"));
        });
        // Nothing about it is saved.
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "skPrefs.writeUi");
    }

    [Fact]
    public async Task Escape_or_a_click_outside_closes_the_open_library_through_the_module()
    {
        await using var context = CreateContext();
        var cut = RenderShell(context);
        cut.Find("[data-drawer-toggle]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-shell][data-library-open]")));

        await cut.InvokeAsync(() => cut.Instance.CloseModalDrawer());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-shell][data-library-open]")));
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

        var twitchMark = twitch.QuerySelector("[data-twitch-mark]");
        Assert.NotNull(twitchMark);
        Assert.Equal("", twitchMark!.GetAttribute("alt"));
        Assert.EndsWith("twitch-glitch.svg", twitchMark.GetAttribute("src"));
    }

    [Fact]
    public async Task Settings_is_a_real_link_in_the_app_bar()
    {
        await using var context = CreateContext();
        var cut = RenderShell(context);

        Assert.Equal("/settings", cut.Find("header a[aria-label='Settings']").GetAttribute("href"));
    }

    [Fact]
    public async Task A_query_change_keeps_the_library_open_and_another_page_closes_it()
    {
        await using var context = CreateContext();
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        var cut = RenderShell(context);
        cut.Find("[data-drawer-toggle]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-shell][data-library-open]")));

        // A filter chip or the search changes only the query: the library stays open.
        navigation.NavigateTo("/?state=saved", replace: true);
        cut.WaitForAssertion(() => Assert.Equal("/?state=saved", "/" + navigation.ToBaseRelativePath(navigation.Uri)));
        Assert.NotNull(cut.Find("[data-shell][data-library-open]"));

        // Picking a recording is another page: it closes, and the watch page shows the rail.
        navigation.NavigateTo("/r/abc");
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-shell][data-library-open]"));
            Assert.Equal("rail", cut.Find("nav[aria-label='Library']").GetAttribute("data-drawer"));
            Assert.NotNull(cut.Find("[data-shell][data-watching]"));
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

    [Fact]
    public async Task Show_library_expands_the_rail_and_applies_the_filter()
    {
        await using var context = CreateContext(
            """{"v":1,"drawer":"rail","theater":false,"stats":false,"details":true,"collapsed":[]}""");
        var cut = RenderShell(context);
        await SetBreakpointAsync(cut, Breakpoint.Lg);

        await cut.InvokeAsync(() => cut.Instance.ShowLibraryAsync("/?state=saved"));

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        Assert.Equal("/?state=saved", "/" + navigation.ToBaseRelativePath(navigation.Uri));
        cut.WaitForAssertion(() =>
            Assert.Equal("expanded", cut.Find("nav[aria-label='Library']").GetAttribute("data-drawer")));
    }

    [Fact]
    public async Task On_a_phone_show_library_opens_the_temporary_drawer_after_navigating()
    {
        await using var context = CreateContext();
        var cut = RenderShell(context);
        await SetBreakpointAsync(cut, Breakpoint.Xs);

        await cut.InvokeAsync(() => cut.Instance.ShowLibraryAsync("/?state=saved"));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-shell][data-drawer-modal-open]")));

        // The next navigation (picking a recording) still closes it.
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/r/abc");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-shell][data-drawer-modal-open]")));
    }

    [Fact]
    public async Task On_a_phone_show_library_opens_the_drawer_when_the_filter_is_already_set()
    {
        await using var context = CreateContext();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/?state=saved");
        var cut = RenderShell(context);
        await SetBreakpointAsync(cut, Breakpoint.Xs);

        await cut.InvokeAsync(() => cut.Instance.ShowLibraryAsync("/?state=saved"));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-shell][data-drawer-modal-open]")));
    }

    [Fact]
    public async Task Settings_opens_over_the_current_page_and_closing_goes_back()
    {
        await using var context = CreateContext();
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/r/abc");
        var cut = RenderShell(context);
        await SetBreakpointAsync(cut, Breakpoint.Lg);

        var toggle = cut.Find("header a[aria-label='Settings']");
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
        // aria-controls names the sheet only while it exists.
        Assert.Null(toggle.GetAttribute("aria-controls"));
        // Closed, nothing is mounted: a closed drawer would be a second, unnamed aside landmark.
        Assert.Empty(cut.FindAll("[id='sk-settings']"));
        Assert.Single(cut.FindAll("aside"));

        await cut.InvokeAsync(cut.Instance.ToggleSettingsAsync);

        // Only the query changes: the page, and its player, stay.
        Assert.Equal("http://localhost/r/abc?settings=open", navigation.Uri);
        cut.WaitForAssertion(() =>
        {
            var sheet = cut.Find("[id='sk-settings']");
            Assert.Equal("Settings", cut.Find($"[id='{sheet.GetAttribute("aria-labelledby")}']").TextContent.Trim());
            Assert.NotNull(sheet.QuerySelector("[data-test='sections']"));
            Assert.Equal("true", cut.Find("header a[aria-label='Settings']").GetAttribute("aria-expanded"));
            Assert.Equal("sk-settings", cut.Find("header a[aria-label='Settings']").GetAttribute("aria-controls"));
            Assert.Contains("Content", cut.Find("main").TextContent);
        });

        // The sheet pushed its entry, so closing it is a step back through history.
        cut.Find("[data-settings-sheet] button[aria-label='Close settings']").Click();
        cut.WaitForAssertion(() => context.JSInterop.VerifyInvoke("history.back"));

        navigation.NavigateTo("/r/abc");
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[id='sk-settings']"));
            Assert.Equal("false", cut.Find("header a[aria-label='Settings']").GetAttribute("aria-expanded"));
        });
    }

    [Fact]
    public async Task The_settings_toggle_closes_an_open_sheet()
    {
        await using var context = CreateContext();
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        // A shared link: nothing to go back to, so closing replaces the entry.
        navigation.NavigateTo("/r/abc?q=nova&settings=open");
        var cut = RenderShell(context);
        await SetBreakpointAsync(cut, Breakpoint.Lg);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-settings-sheet]")));

        await cut.InvokeAsync(cut.Instance.ToggleSettingsAsync);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("http://localhost/r/abc?q=nova", navigation.Uri);
            Assert.Empty(cut.FindAll("[data-settings-sheet]"));
        });
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "history.back");
    }

    [Fact]
    public async Task On_the_settings_page_the_sheet_never_opens_and_settings_is_a_plain_link()
    {
        await using var context = CreateContext();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/settings?settings=open");
        var cut = RenderShell(context);
        await SetBreakpointAsync(cut, Breakpoint.Lg);

        Assert.Empty(cut.FindAll("[data-settings-sheet]"));
        var link = cut.Find("header a[aria-label='Settings']");
        Assert.Equal("/settings", link.GetAttribute("href"));
        Assert.Null(link.GetAttribute("aria-expanded"));
    }

    [Fact]
    public async Task On_a_phone_settings_is_a_full_screen_dialog_with_a_now_playing_strip()
    {
        await using var context = CreateContext();
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/r/abc");
        var dialogs = context.Render<MudDialogProvider>();
        var cut = RenderShell(context, parameters => parameters
            .Add(component => component.NowPlaying, Playing));
        await SetBreakpointAsync(cut, Breakpoint.Xs);

        await cut.InvokeAsync(cut.Instance.ToggleSettingsAsync);

        dialogs.WaitForAssertion(() =>
        {
            var dialog = dialogs.OpenDialog("Settings");
            Assert.NotNull(dialog);
            Assert.NotNull(dialog!.QuerySelector("[data-test='sections']"));
            Assert.NotNull(dialog.QuerySelector("button[aria-label='Close settings']"));
            var strip = dialog.QuerySelector("[data-now-playing]")!;
            Assert.Equal("Now playing", strip.GetAttribute("aria-label"));
            Assert.Contains("Ranked climb to Diamond", strip.TextContent);
            Assert.Contains("nova", strip.TextContent);
            Assert.Equal("/media/nova/abc/thumb.jpg", strip.QuerySelector("img")!.GetAttribute("src"));
            Assert.NotNull(strip.QuerySelector("button[data-now-playing-toggle]"));
        });
        // Only the dialog: no desktop sheet beside it.
        Assert.Empty(cut.FindAll("[data-settings-sheet]"));

        dialogs.Find("[data-now-playing] button[aria-label='Back to the video']").Click();
        cut.WaitForAssertion(() => context.JSInterop.VerifyInvoke("history.back"));
        navigation.NavigateTo("/r/abc");
        dialogs.WaitForAssertion(() => Assert.False(dialogs.HasOpenDialog("Settings")));
    }

    [Fact]
    public async Task On_a_phone_the_bottom_bar_settings_shows_the_open_sheet()
    {
        await using var context = CreateContext();
        context.Render<MudDialogProvider>();
        var cut = RenderShell(context);
        await SetBreakpointAsync(cut, Breakpoint.Xs);

        var settings = cut.Find("nav[aria-label='Main'] a[href='/settings']");
        Assert.Equal("false", settings.GetAttribute("aria-expanded"));
        // The sheet's module returns focus here when the app bar's Settings is hidden (phones).
        Assert.True(settings.HasAttribute("data-settings-toggle"));
        Assert.Null(settings.GetAttribute("aria-controls"));

        await cut.InvokeAsync(cut.Instance.ToggleSettingsAsync);

        cut.WaitForAssertion(() =>
        {
            var open = cut.Find("nav[aria-label='Main'] a[href='/settings']");
            Assert.Equal("true", open.GetAttribute("aria-expanded"));
            Assert.Equal("sk-settings", open.GetAttribute("aria-controls"));
            Assert.Equal("page", open.GetAttribute("aria-current"));
            Assert.Null(cut.Find("nav[aria-label='Main'] a[href='/']").GetAttribute("aria-current"));
        });
    }

    [Fact]
    public async Task A_sheet_open_before_the_breakpoint_is_known_moves_into_the_phone_dialog()
    {
        // A phone reloading ?settings=open: the server renders the (CSS-hidden) drawer, then the
        // breakpoint swaps it for the dialog, and the module must follow the dialog.
        await using var context = CreateContext();
        BunitJSModuleInterop module = context.JSInterop.SetupModule("./Components/Features/Settings/SettingsSheet.razor.js");
        module.Mode = JSRuntimeMode.Loose;
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/r/abc?settings=open");
        var dialogs = context.Render<MudDialogProvider>();
        var cut = RenderShell(context);
        cut.WaitForAssertion(() => Assert.Single(module.Invocations, call => call.Identifier == "opened"));

        await SetBreakpointAsync(cut, Breakpoint.Xs);

        dialogs.WaitForAssertion(() => Assert.True(dialogs.HasOpenDialog("Settings")));
        cut.WaitForAssertion(() => Assert.Equal(2, module.Invocations.Count(call => call.Identifier == "opened")));
    }

    private static readonly RecordingView Playing = new(
        "abc", "nova", "Ranked climb to Diamond", "Games", DateTimeOffset.UnixEpoch, null,
        3280, RecordingState.Recording, 8, 1, 1024, "/media/nova/abc/thumb.jpg",
        "/media/nova/abc/index.m3u8", null, []);

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
            parameters.Add(component => component.SettingsContent, fragment =>
                fragment.AddMarkupContent(0, "<div data-test='sections'>Sections</div>"));
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
