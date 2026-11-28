using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MudBlazor.Services;
using StreamKeepr.Components.Features.Library;
using StreamKeepr.Components.Layout;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

/// <summary>The library's channel index and recordings pane (design system: LibraryDrawer): the
/// channels listbox and the selected channel's recordings, with Delete on each row
/// (RecordingRow). Replaces the tree.</summary>
public sealed class LibraryChannelsTests
{
    private const string KeysModule = "./js/library-keys.js";
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_channels_are_a_single_select_listbox_with_one_tab_stop_and_no_rows_until_one_is_picked()
    {
        await using BunitContext context = CreateContext(out _);

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        var listbox = cut.Find("[role='listbox']");
        Assert.Equal("Channels", listbox.GetAttribute("aria-label"));
        var options = cut.FindAll("[role='option']");
        Assert.Equal(["alpha", "beta"], options.Select(option => option.GetAttribute("data-library-channel")));
        Assert.All(options, option => Assert.Equal("false", option.GetAttribute("aria-selected")));
        Assert.Equal(["0", "-1"], options.Select(option => option.GetAttribute("tabindex")));
        Assert.Equal("2", Option(cut, "alpha").QuerySelector("[data-channel-count]")!.TextContent);
        Assert.Empty(cut.FindAll("[data-recordings-column]"));
        Assert.Empty(cut.FindAll("a[data-recording-id]"));
    }

    [Fact]
    public async Task Wide_the_pane_asks_for_a_channel_until_one_is_picked()
    {
        await using BunitContext context = CreateContext(out _);
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        var prompt = cut.Find("[data-recordings-prompt]");
        Assert.Contains("Choose a channel", prompt.TextContent);
        Assert.Contains("ready to watch or delete", prompt.TextContent);
        Assert.Empty(cut.FindAll("[data-recordings-column]"));

        Option(cut, "alpha").Click();

        Assert.Empty(cut.FindAll("[data-recordings-prompt]"));
        Assert.Equal("Alpha", cut.Find("[data-recordings-column='alpha'] h3").TextContent);
        Assert.Single(cut.FindAll("[data-recordings-column='alpha'] [data-channel-color]"));
    }

    [Fact]
    public async Task The_closed_rail_ignores_search_and_filter()
    {
        var view = View();
        await using BunitContext context = CreateContext(out _, view);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/?q=zzzz-no-match&state=saved");

        var cut = context.Render<CascadingValue<bool>>(parameters => parameters
            .Add(component => component.Name, "LibraryRail")
            .Add(component => component.Value, true)
            .AddChildContent<LibraryDrawer>());

        Assert.Equal(
            ["alpha", "beta"],
            cut.FindAll("[data-library-channel]").Select(option => option.GetAttribute("data-library-channel")));
        Assert.Empty(cut.FindAll("[data-library-no-match]"));
        Assert.Empty(cut.FindAll("[data-library-empty]"));
        Assert.Empty(cut.FindAll("[data-recordings-prompt]"));
        Assert.NotNull(view.LastQuery);
        Assert.Null(view.LastQuery.Search);
        Assert.Null(view.LastQuery.State);
    }

    [Fact]
    public async Task Picking_a_channel_opens_its_pane_and_close_returns_to_the_channels()
    {
        await using BunitContext context = CreateContext(out _);
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Option(cut, "alpha").Click();

        Assert.Equal("true", Option(cut, "alpha").GetAttribute("aria-selected"));
        Assert.Equal("0", Option(cut, "alpha").GetAttribute("tabindex"));
        var column = cut.Find("[data-recordings-column='alpha']");
        Assert.Equal("Alpha", column.QuerySelector("h3")!.TextContent);
        Assert.Equal("2 recordings", column.QuerySelector("[data-column-meta]")!.TextContent);
        Assert.Equal(["a1", "a2"], cut.FindAll("[role='list'] [role='listitem'] a[data-recording-id]").Select(a => a.GetAttribute("data-recording-id")));
        Assert.All(cut.FindAll("[data-day-header]"), day => Assert.Equal("presentation", day.GetAttribute("role")));

        // Switching channels swaps the pane's contents.
        Option(cut, "beta").Click();
        Assert.Single(cut.FindAll("[data-recordings-column='beta']"));
        Assert.Empty(cut.FindAll("[data-recordings-column='alpha']"));

        cut.Find("button[aria-label=\"Close Beta's recordings\"]").Click();

        Assert.Empty(cut.FindAll("[data-recordings-column]"));
        Assert.Equal("false", Option(cut, "beta").GetAttribute("aria-selected"));
    }

    [Fact]
    public async Task A_channel_the_search_drops_closes_its_pane()
    {
        var view = View();
        await using BunitContext context = CreateContext(out _, view);
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();
        Option(cut, "beta").Click();

        view.StreamerItems = [view.StreamerItems[0]];
        navigation.NavigateTo("/?q=alpha");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-recordings-column]")));
    }

    [Fact]
    public async Task The_library_opens_on_the_watched_channel_on_a_watch_page_and_on_nothing_elsewhere()
    {
        var view = View();
        view.FindById["b1"] = view.RecordingsByLogin["beta"][0];
        await using BunitContext context = CreateContext(out _, view);
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        IRenderedComponent<CascadingValue<bool>> cut = RenderOpen(context, open: true);

        // Home: it opens on nothing, and a pick lasts only until it closes.
        Assert.Empty(cut.FindAll("[data-recordings-column]"));
        cut.Find("[data-library-channel='alpha']").Click();
        Assert.Single(cut.FindAll("[data-recordings-column='alpha']"));
        SetOpen(cut, open: false);
        Assert.Empty(cut.FindAll("[data-recordings-column]"));

        // Watching b1, it opens on b1's channel, and goes back to it after another pick.
        navigation.NavigateTo("/r/b1");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-recordings-column='beta']")));
        SetOpen(cut, open: true);
        cut.Find("[data-library-channel='alpha']").Click();
        SetOpen(cut, open: false);
        Assert.Single(cut.FindAll("[data-recordings-column='beta']"));
        Assert.Equal("true", cut.Find("[data-library-channel='beta']").GetAttribute("aria-selected"));

        // Back on another page, nothing is selected again.
        navigation.NavigateTo("/settings");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-recordings-column]")));
    }

    [Fact]
    public async Task An_open_library_keeps_its_channel_while_only_the_query_changes()
    {
        await using BunitContext context = CreateContext(out _);
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        IRenderedComponent<CascadingValue<bool>> cut = RenderOpen(context, open: true);
        cut.Find("[data-library-channel='alpha']").Click();

        navigation.NavigateTo("/?sort=oldest");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-recordings-column='alpha']")));
    }

    [Fact]
    public async Task A_deep_link_opens_on_its_channel_once_the_library_has_loaded()
    {
        var view = View();
        view.IsLoaded = false;
        await using BunitContext context = CreateContext(out _, view);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/r/b1");
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        view.FindById["b1"] = view.RecordingsByLogin["beta"][0];
        view.IsLoaded = true;
        view.RaiseChanged();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-recordings-column='beta']")));
    }

    [Fact]
    public async Task Below_md_the_pane_drills_in_with_all_channels_and_reopens_on_the_watched_channel()
    {
        var view = View();
        view.FindById["b1"] = view.RecordingsByLogin["beta"][0];
        await using BunitContext context = CreateContext(out _, view);
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        IRenderedComponent<CascadingValue<bool>> cut = RenderCompact(context);

        // There is no room for a second pane below Md; the index is the only first step.
        Assert.Empty(cut.FindAll("[data-recordings-prompt]"));

        cut.Find("[data-library-channel='alpha']").Click();
        Assert.NotNull(cut.Find("[data-recordings-column='alpha'] button[aria-label='All channels']"));
        Assert.Empty(cut.FindAll("[data-recordings-column] button[aria-label^='Close']"));

        navigation.NavigateTo("/r/b1");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-recordings-column='beta']")));
        cut.Find("button[aria-label='All channels']").Click();
        Assert.Empty(cut.FindAll("[data-recordings-column]"));
    }

    [Fact]
    public async Task A_rail_avatar_opens_the_drawer_on_that_channel_and_clears_a_stale_query()
    {
        var view = View();
        await using BunitContext context = CreateContext(out _, view);
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/?q=zzzz-no-match&state=saved");

        var cut = context.Render<CascadingValue<bool>>(parameters => parameters
            .Add(component => component.Name, "LibraryRail")
            .Add(component => component.Value, true)
            .AddChildContent<LibraryDrawer>());
        // The rail's avatar names the channel, 32px (MudAvatar Size.Medium), and opens no pane.
        Assert.Equal("Beta", cut.Find("[data-library-channel='beta'] [role='img']").GetAttribute("aria-label"));
        Assert.All(cut.FindComponents<ChannelAvatar>(), avatar => Assert.Equal(MudBlazor.Size.Medium, avatar.Instance.Size));

        cut.Find("[data-library-channel='beta']").Click();

        // The rail shows every channel, but its pick must clear the stale search/state before
        // the drawer expands; otherwise the chosen channel's pane cannot be shown.
        Assert.DoesNotContain("q=", new Uri(navigation.Uri).Query, StringComparison.Ordinal);
        Assert.DoesNotContain("state=", new Uri(navigation.Uri).Query, StringComparison.Ordinal);

        // The shell opens the library (MainLayout test below); it is the same drawer, which
        // keeps the channel picked in the rail.
        LibraryDrawer before = cut.FindComponent<LibraryDrawer>().Instance;
        cut.Render(parameters => parameters
            .Add(component => component.Value, false)
            .AddChildContent<LibraryDrawer>());
        Assert.Same(before, cut.FindComponent<LibraryDrawer>().Instance);
        Assert.Single(cut.FindAll("[data-recordings-column='beta']"));
    }

    [Fact]
    public async Task Initializes_the_library_keys_and_resets_the_scroll_only_on_query_changes()
    {
        await using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Assert.Contains(module.Invocations, call => call.Identifier == "init");

        navigation.NavigateTo("/r/a1");
        cut.WaitForAssertion(() => Assert.Equal("/r/a1", new Uri(navigation.Uri).AbsolutePath));
        Assert.DoesNotContain(module.Invocations, call => call.Identifier == "scrollToTop");

        navigation.NavigateTo("/r/a1?state=saved");
        cut.WaitForAssertion(() =>
            Assert.Contains(module.Invocations, call => call.Identifier == "scrollToTop"));
    }

    [Fact]
    public async Task The_drawer_header_carries_the_quiet_paused_note()
    {
        await using BunitContext context = CreateContext(out _);

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        var note = cut.Find("[data-updates-paused]");
        Assert.Equal("Live updates paused", note.QuerySelector("[data-updates-paused-msg]")!.TextContent);
        Assert.Equal("status", note.GetAttribute("role"));
    }

    [Fact]
    public async Task Every_row_has_its_own_delete_button_beside_the_link_not_inside_it()
    {
        await using BunitContext context = CreateContext(out _, signedIn: true);
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();
        Option(cut, "alpha").Click();

        var button = cut.Find("[data-recording-id='a1'] ~ button[data-row-delete]");
        Assert.Equal("Delete recording: Title a1", button.GetAttribute("aria-label"));
        Assert.Equal("Delete recording", button.GetAttribute("data-tip"));
        Assert.Null(button.GetAttribute("aria-disabled"));
        Assert.Empty(cut.FindAll("a[data-recording-id] button"));
    }

    [Theory]
    [InlineData(false, RecordingState.Saved, false, "Connect Twitch to use this")]
    [InlineData(true, RecordingState.Recording, false, "Available when the recording finishes")]
    [InlineData(true, RecordingState.Reconnecting, false, "Available when the recording finishes")]
    [InlineData(true, RecordingState.Saved, true, "Cancel the export before deleting")]
    public async Task A_blocked_delete_stays_visible_disabled_and_says_why(
        bool signedIn, RecordingState state, bool exporting, string reason)
    {
        var view = View();
        view.RecordingsByLogin["alpha"] = [Recording("a1", "alpha", state)];
        await using BunitContext context = CreateContext(out _, view, signedIn);
        if (exporting)
        {
            ((TestExportService)context.Services.GetRequiredService<IExportService>())
                .Set(new ExportJob("e1", "a1", ExportState.Running, 0.5, null, null));
        }
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();
        Option(cut, "alpha").Click();
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();

        var button = cut.Find("button[data-row-delete]");
        Assert.Equal("true", button.GetAttribute("aria-disabled"));
        Assert.Equal(reason, button.GetAttribute("data-tip"));
        Assert.Equal(reason, cut.Find($"#{button.GetAttribute("aria-describedby")}").TextContent);

        button.Click();

        Assert.False(dialogs.HasOpenDialog("Delete recording?"));
    }

    [Fact]
    public async Task Delete_confirms_deletes_and_says_so_leaving_the_drawer_open()
    {
        var view = View();
        await using BunitContext context = CreateContext(out BunitJSModuleInterop module, view, signedIn: true);
        var admin = (TestRecordingAdmin)context.Services.GetRequiredService<IRecordingAdmin>();
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();
        Option(cut, "alpha").Click();

        cut.Find("[data-recording-id='a1'] ~ button[data-row-delete]").Click();
        dialogs.WaitForAssertion(() => Assert.True(dialogs.HasOpenDialog("Delete recording?")));
        Assert.Contains("Title a1 — alpha", dialogs.Find("[data-delete-banner]").TextContent);
        // The row is watched from here, so it can collapse out once the library drops it.
        Assert.Contains(module.Invocations,
            call => call.Identifier == "armRowCollapse" && Equals(call.Arguments[1], "a1"));

        dialogs.Find("[data-delete-confirm]").Click();

        cut.WaitForAssertion(() => Assert.Single(admin.Deletes));
        Assert.Equal("a1", admin.Deletes[0].RecordingId);
        Assert.Equal("Recording deleted", context.Services.GetRequiredService<MudBlazor.ISnackbar>().CurrentMessage());
        Assert.Single(cut.FindAll("[data-recordings-column='alpha']"));
        // Focus goes to the row that takes its place, once the library has dropped it.
        cut.WaitForAssertion(() => Assert.Contains(
            module.Invocations,
            call => call.Identifier == "focusAfterDelete" && Equals(call.Arguments[1], 0) && Equals(call.Arguments[2], "a1")));
    }

    [Fact]
    public async Task Cancelling_delete_stops_watching_the_row()
    {
        var view = View();
        view.FindById["a1"] = view.RecordingsByLogin["alpha"][0];
        await using BunitContext context = CreateContext(out BunitJSModuleInterop module, view, signedIn: true);
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();
        Option(cut, "alpha").Click();
        cut.Find("[data-recording-id='a1'] ~ button[data-row-delete]").Click();
        dialogs.WaitForAssertion(() => Assert.True(dialogs.HasOpenDialog("Delete recording?")));

        dialogs.Find("[data-delete-cancel]").Click();

        cut.WaitForAssertion(() => Assert.Contains(module.Invocations, call => call.Identifier == "disarmRowCollapse"));
        Assert.Single(cut.FindAll("[data-recording-id='a1']"));
    }

    [Fact]
    public async Task Deleting_the_watched_recording_leaves_the_announcement_to_the_watch_page()
    {
        var view = View();
        view.FindById["a1"] = view.RecordingsByLogin["alpha"][0];
        await using BunitContext context = CreateContext(out _, view, signedIn: true);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/r/a1");
        var dialogs = context.Render<MudBlazor.MudDialogProvider>();
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();
        Option(cut, "alpha").Click();

        cut.Find("[data-recording-id='a1'] ~ button[data-row-delete]").Click();
        dialogs.WaitForAssertion(() => Assert.True(dialogs.HasOpenDialog("Delete recording?")));
        dialogs.Find("[data-delete-confirm]").Click();

        var admin = (TestRecordingAdmin)context.Services.GetRequiredService<IRecordingAdmin>();
        cut.WaitForAssertion(() => Assert.Single(admin.Deletes));
        Assert.Null(context.Services.GetRequiredService<MudBlazor.ISnackbar>().CurrentMessage());
    }

    private static AngleSharp.Dom.IElement Option(IRenderedComponent<LibraryDrawer> cut, string login) =>
        cut.Find($"[data-library-channel='{login}']");

    // The shell's LibraryOpen cascade, which the test opens and closes.
    private static IRenderedComponent<CascadingValue<bool>> RenderOpen(BunitContext context, bool open) =>
        context.Render<CascadingValue<bool>>(parameters => parameters
            .Add(component => component.Name, AppShell.LibraryOpenCascade)
            .Add(component => component.Value, open)
            .AddChildContent<LibraryDrawer>());

    private static void SetOpen(IRenderedComponent<CascadingValue<bool>> cut, bool open) =>
        cut.Render(parameters => parameters
            .Add(component => component.Value, open)
            .AddChildContent<LibraryDrawer>());

    private static IRenderedComponent<CascadingValue<bool>> RenderCompact(BunitContext context) =>
        context.Render<CascadingValue<bool>>(parameters => parameters
            .Add(component => component.Name, AppShell.CompactCascade)
            .Add(component => component.Value, true)
            .AddChildContent<LibraryDrawer>());

    private static TestLibraryView View()
    {
        var view = new TestLibraryView
        {
            StreamerItems =
            [
                new StreamerView("alpha", "Alpha", null, false, false, true, 2, Now),
                new StreamerView("beta", "Beta", null, false, false, true, 1, Now.AddDays(-1)),
            ],
        };
        view.RecordingsByLogin["alpha"] = [Recording("a1", "alpha"), Recording("a2", "alpha")];
        view.RecordingsByLogin["beta"] = [Recording("b1", "beta")];
        return view;
    }

    private static BunitContext CreateContext(
        out BunitJSModuleInterop module, TestLibraryView? view = null, bool signedIn = false)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        module = context.JSInterop.SetupModule(KeysModule);
        module.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();
        context.Services.AddStreamKeeprMudServices();
        context.Services.AddSingleton<IClientStateStore>(new TestClientStateStore());
        context.Services.AddScoped<ViewerClock>();
        context.Services.AddScoped<UiPreferences>();
        context.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        context.Services.AddSingleton<ILibraryView>(view ?? View());
        context.Services.AddSingleton<IExportService>(new TestExportService());
        context.Services.AddSingleton<IRecordingAdmin>(new TestRecordingAdmin());
        BunitAuthorizationContext auth = context.AddAuthorization();
        if (signedIn)
            auth.SetAuthorized("mock_admin");
        else
            auth.SetNotAuthorized();
        return context;
    }

    private static RecordingView Recording(string id, string login, RecordingState state = RecordingState.Saved) =>
        new(id, login, $"Title {id}", "Game", Now.AddHours(-2), Now.AddHours(-1), 3600,
            state, 8, 1, 1024, null, $"/media/{login}/{id}/index.m3u8", null, []);
}
