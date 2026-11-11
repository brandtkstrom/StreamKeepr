using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Components.Features.Library;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class LibraryTreeTests
{
    private const string TreeModule = "./js/tree-nav.js";
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Renders_one_tree_with_levels_expansion_and_keys()
    {
        using BunitContext context = CreateContext(out _);
        context.Services.GetRequiredService<UiPreferences>().SetCollapsed("beta", true);

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        var tree = cut.Find("[role='tree']");
        Assert.Equal("Recordings", tree.GetAttribute("aria-label"));

        var heads = cut.FindAll("[role='treeitem'][aria-level='1']");
        Assert.Equal(["g:alpha", "g:beta"], heads.Select(head => head.GetAttribute("data-tree-key")));
        Assert.Equal("true", heads[0].GetAttribute("aria-expanded"));
        Assert.Equal("false", heads[1].GetAttribute("aria-expanded"));

        var rows = cut.FindAll("a.sk-row[role='treeitem'][aria-level='2']");
        Assert.Equal(["r:a1", "r:b1"], rows.Select(row => row.GetAttribute("data-tree-key")));

        // Collapsed rows leave the accessibility tree and the focus order.
        Assert.Null(cut.Find("#sk-group-alpha").GetAttribute("inert"));
        Assert.NotNull(cut.Find("#sk-group-beta").GetAttribute("inert"));
        Assert.All(cut.FindAll(".sk-day"), day => Assert.Equal("none", day.GetAttribute("role")));
    }

    [Fact]
    public void Initializes_tree_navigation_on_the_list()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);

        context.Render<LibraryDrawer>();

        Assert.Contains(module.Invocations, call => call.Identifier == "init");
        Assert.Contains(module.Invocations, call => call.Identifier == "revealCurrent");
    }

    [Fact]
    public void Activating_an_avatar_in_the_rail_expands_the_drawer_and_scrolls_to_the_group()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        module.Setup<bool>("isRail", _ => true).SetResult(true);
        UiPreferences preferences = context.Services.GetRequiredService<UiPreferences>();
        preferences.SetDrawer(DrawerMode.Rail);
        preferences.SetCollapsed("beta", true);

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();
        // The avatar's own label names the streamer in the rail.
        Assert.Equal("Beta", Head(cut, "beta").QuerySelector("[role='img']")!.GetAttribute("aria-label"));

        Head(cut, "beta").Click();

        Assert.Equal(DrawerMode.Expanded, preferences.Drawer);
        Assert.False(preferences.IsCollapsed("beta"));
        cut.WaitForAssertion(() => Assert.Contains(
            module.Invocations,
            call => call.Identifier == "scrollToKey" && Equals(call.Arguments[1], "g:beta")));
    }

    [Fact]
    public void Outside_the_rail_activating_a_head_toggles_its_group()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        module.Setup<bool>("isRail", _ => true).SetResult(false);
        UiPreferences preferences = context.Services.GetRequiredService<UiPreferences>();

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        Head(cut, "alpha").Click();

        Assert.True(preferences.IsCollapsed("alpha"));
        Assert.Equal(DrawerMode.Expanded, preferences.Drawer);
        cut.WaitForAssertion(() =>
            Assert.Equal("false", Head(cut, "alpha").GetAttribute("aria-expanded")));
    }

    [Fact]
    public void Query_changes_reset_the_scroll_but_selection_changes_do_not()
    {
        using BunitContext context = CreateContext(out BunitJSModuleInterop module);
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        navigation.NavigateTo("/r/a1");
        cut.WaitForAssertion(() =>
            Assert.Equal("true", cut.Find("[data-recording-id='a1']").GetAttribute("aria-current")));
        Assert.DoesNotContain(module.Invocations, call => call.Identifier == "scrollToTop");

        navigation.NavigateTo("/r/a1?state=saved");
        cut.WaitForAssertion(() =>
            Assert.Contains(module.Invocations, call => call.Identifier == "scrollToTop"));
    }

    [Fact]
    public void The_drawer_header_carries_the_quiet_paused_note()
    {
        using BunitContext context = CreateContext(out _);

        IRenderedComponent<LibraryDrawer> cut = context.Render<LibraryDrawer>();

        var note = cut.Find(".sk-drawer-note[data-updates-paused]");
        Assert.Equal("Live updates paused", note.QuerySelector(".msg")!.TextContent);
        Assert.Equal("status", note.GetAttribute("role"));
    }

    private static AngleSharp.Dom.IElement Head(IRenderedComponent<LibraryDrawer> cut, string login) =>
        cut.Find($"[data-tree-key='g:{login}']");

    private static BunitContext CreateContext(out BunitJSModuleInterop module)
    {
        var view = new TestLibraryView
        {
            StreamerItems =
            [
                new StreamerView("alpha", "Alpha", null, false, false, true, 1, Now),
                new StreamerView("beta", "Beta", null, false, false, true, 1, Now.AddDays(-1)),
            ],
        };
        view.RecordingsByLogin["alpha"] = [Recording("a1", "alpha")];
        view.RecordingsByLogin["beta"] = [Recording("b1", "beta")];

        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        module = context.JSInterop.SetupModule(TreeModule);
        module.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();
        context.Services.AddSingleton<IClientStateStore>(new TestClientStateStore());
        context.Services.AddScoped<ViewerClock>();
        context.Services.AddScoped<SnackbarService>();
        context.Services.AddScoped<UiPreferences>();
        context.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        context.Services.AddSingleton<ILibraryView>(view);
        return context;
    }

    private static RecordingView Recording(string id, string login) =>
        new(id, login, $"Title {id}", "Game", Now.AddHours(-2), Now.AddHours(-1), 3600,
            RecordingState.Saved, 8, 1, 1024, null, $"/media/{login}/{id}/index.m3u8", null, []);
}
