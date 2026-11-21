using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class UiPreferencesTests
{
    private const string DefaultJson =
        """{"v":1,"drawer":"expanded","theater":false,"stats":false,"details":true,"collapsed":[]}""";

    [Fact]
    public void Missing_cookie_uses_defaults()
    {
        var preferences = Create();

        Assert.Equal(DrawerMode.Expanded, preferences.Drawer);
        Assert.False(preferences.Theater);
        Assert.False(preferences.StatsOpen);
        Assert.True(preferences.DetailsOpen);
        Assert.Empty(preferences.CollapsedStreamers);
    }

    [Fact]
    public void Parses_the_versioned_cookie()
    {
        var preferences = Create(
            """{"v":1,"drawer":"rail","theater":true,"stats":true,"details":false,"collapsed":["pixelwren"]}""");

        Assert.Equal(DrawerMode.Rail, preferences.Drawer);
        Assert.True(preferences.Theater);
        Assert.True(preferences.StatsOpen);
        Assert.False(preferences.DetailsOpen);
        Assert.True(preferences.IsCollapsed("pixelwren"));
    }

    [Fact]
    public void Encoded_cookie_is_decoded()
    {
        var json = """{"v":1,"drawer":"hidden","theater":false,"stats":false,"details":true,"collapsed":[]}""";
        var preferences = Create(Uri.EscapeDataString(json));

        Assert.Equal(DrawerMode.Hidden, preferences.Drawer);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"v":2,"drawer":"rail"}""")]
    [InlineData("""{"drawer":"rail"}""")]
    public void Malformed_or_unknown_version_falls_back_to_defaults(string cookie)
    {
        var preferences = Create(cookie);

        Assert.Equal(DrawerMode.Expanded, preferences.Drawer);
        Assert.True(preferences.DetailsOpen);
    }

    [Fact]
    public void Unknown_fields_and_invalid_values_are_ignored()
    {
        var preferences = Create(
            """{"v":1,"drawer":"sideways","theater":null,"stats":true,"details":false,"extra":"ignored","collapsed":[null,"","pixelwren","PIXELWREN"]}""");

        Assert.Equal(DrawerMode.Expanded, preferences.Drawer);
        Assert.False(preferences.Theater);
        Assert.True(preferences.StatsOpen);
        Assert.False(preferences.DetailsOpen);
        Assert.Single(preferences.CollapsedStreamers);
    }

    [Fact]
    public void Collapsed_streamers_are_capped_to_the_newest_fifty()
    {
        var collapsed = Enumerable.Range(0, 60).Select(index => $"login{index}").ToArray();
        var cookie =
            $$"""{"v":1,"drawer":"expanded","theater":false,"stats":false,"details":true,"collapsed":[{{string.Join(',', collapsed.Select(login => $"\"{login}\""))}}]}""";

        var preferences = Create(cookie);

        Assert.Equal(UiPreferences.MaxCollapsedStreamers, preferences.CollapsedStreamers.Count);
        Assert.False(preferences.IsCollapsed("login9"));
        Assert.True(preferences.IsCollapsed("login10"));
        Assert.True(preferences.IsCollapsed("login59"));
    }

    [Fact]
    public void Cookie_size_is_capped_at_four_kilobytes()
    {
        var collapsed = Enumerable.Range(0, 50)
            .Select(index => $"{index:D2}{new string('x', 120)}")
            .ToArray();
        var cookie =
            $$"""{"v":1,"drawer":"expanded","theater":false,"stats":false,"details":true,"collapsed":[{{string.Join(',', collapsed.Select(login => $"\"{login}\""))}}]}""";

        var preferences = Create(cookie);
        var json = string.Join(
            ',',
            preferences.CollapsedStreamers.Select(login => $"\"{login}\""));
        var shape =
            $$"""{"v":1,"drawer":"{{preferences.Drawer.ToString().ToLowerInvariant()}}","theater":{{preferences.Theater.ToString().ToLowerInvariant()}},"stats":{{preferences.StatsOpen.ToString().ToLowerInvariant()}},"details":{{preferences.DetailsOpen.ToString().ToLowerInvariant()}},"collapsed":[{{json}}]}""";

        Assert.True(preferences.CollapsedStreamers.Count < UiPreferences.MaxCollapsedStreamers);
        Assert.True(Encoding.UTF8.GetByteCount(shape) <= UiPreferences.MaxCookieBytes);
    }

    [Fact]
    public void Setters_raise_changed_only_when_state_changes()
    {
        var preferences = Create();
        var changes = 0;
        preferences.Changed += () => changes++;

        preferences.ToggleDrawer();
        preferences.ToggleDrawer();
        preferences.SetTheater(true);
        preferences.SetTheater(true);
        preferences.SetStatsOpen(true);
        preferences.SetDetailsOpen(false);
        preferences.SetCollapsed("pixelwren", true);
        preferences.SetCollapsed("pixelwren", true);
        preferences.SetCollapsed("pixelwren", false);

        Assert.Equal(7, changes);
    }

    [Fact]
    public void Chat_sync_defaults_to_zero_and_reads_the_cookie()
    {
        Assert.Equal(0, Create().ChatSyncOffset);
        Assert.Equal(-4.5, Create("""{"v":1,"chatSync":-4.5}""").ChatSyncOffset);
    }

    [Theory]
    [InlineData(45, 30)]
    [InlineData(-45, -30)]
    [InlineData(1.3, 1.5)]
    [InlineData(-1.2, -1)]
    [InlineData(0.25, 0.5)]
    public void Chat_sync_is_clamped_to_the_endpoint_limit_and_snapped_to_half_seconds(double value, double expected)
    {
        var preferences = Create();
        preferences.SetChatSyncOffset(value);

        Assert.Equal(expected, preferences.ChatSyncOffset);
        string cookie = $$"""{"v":1,"chatSync":{{value.ToString(CultureInfo.InvariantCulture)}}}""";
        Assert.Equal(expected, Create(cookie).ChatSyncOffset);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_non_finite_chat_sync_is_ignored(double value)
    {
        var preferences = Create();
        preferences.SetChatSyncOffset(2);
        var changes = 0;
        preferences.Changed += () => changes++;

        preferences.SetChatSyncOffset(value);

        Assert.Equal(2, preferences.ChatSyncOffset);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Chat_sync_raises_changed_only_when_the_snapped_value_changes()
    {
        var preferences = Create();
        var changes = 0;
        preferences.Changed += () => changes++;

        preferences.SetChatSyncOffset(1.5);
        preferences.SetChatSyncOffset(1.4);
        preferences.SetChatSyncOffset(-2);

        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Persist_writes_the_chat_sync_offset()
    {
        var js = new RecordingJSRuntime();
        var preferences = Create(js: js);
        preferences.SetChatSyncOffset(-3.5);

        await preferences.PersistAsync();

        var state = Assert.IsType<UiPreferencesState>(Assert.Single(Assert.Single(js.Invocations).Arguments));
        Assert.Equal(-3.5, state.ChatSync);
    }

    [Fact]
    public async Task Persist_writes_the_same_versioned_shape_through_prefs_js()
    {
        var js = new RecordingJSRuntime();
        var preferences = Create(js: js);
        preferences.ToggleDrawer();
        preferences.SetCollapsed("pixelwren", true);

        await preferences.PersistAsync();

        var invocation = Assert.Single(js.Invocations);
        Assert.Equal("skPrefs.writeUi", invocation.Identifier);
        var state = Assert.IsType<UiPreferencesState>(Assert.Single(invocation.Arguments));
        Assert.Equal(1, state.Version);
        Assert.Equal("rail", state.Drawer);
        Assert.True(state.Details);
        Assert.Equal(new[] { "pixelwren" }, state.Collapsed);
    }

    [Fact]
    public async Task Persist_swallows_js_interop_failures()
    {
        var preferences = Create(js: new ThrowingJSRuntime());

        var exception = await Record.ExceptionAsync(preferences.PersistAsync);

        Assert.Null(exception);
    }

    [Fact]
    public void Prerender_state_is_persisted_and_restored_into_the_circuit()
    {
        var prerenderStore = new TestClientStateStore();
        prerenderStore.Cookies[UiPreferences.CookieName] =
            """{"v":1,"drawer":"hidden","theater":true,"stats":true,"details":false,"collapsed":["mossbyte"]}""";

        _ = Create(store: prerenderStore);
        var persisted = Assert.IsType<string>(prerenderStore.Persisted[UiPreferences.CookieName]);

        var circuitStore = new TestClientStateStore();
        circuitStore.Persist(UiPreferences.CookieName, persisted);
        var preferences = Create(store: circuitStore);

        Assert.Equal(DrawerMode.Hidden, preferences.Drawer);
        Assert.True(preferences.Theater);
        Assert.True(preferences.StatsOpen);
        Assert.False(preferences.DetailsOpen);
        Assert.True(preferences.IsCollapsed("mossbyte"));
    }

    [Fact]
    public void Watch_pages_default_to_the_rail_and_home_to_the_full_library()
    {
        var preferences = Create();

        Assert.Equal(DrawerMode.Rail, preferences.WatchDrawer);
        Assert.Equal(DrawerMode.Rail, preferences.DrawerFor(watching: true));
        Assert.Equal(DrawerMode.Expanded, preferences.DrawerFor(watching: false));
    }

    [Theory]
    [InlineData("expanded", DrawerMode.Expanded)]
    [InlineData("rail", DrawerMode.Rail)]
    [InlineData("hidden", DrawerMode.Hidden)]
    [InlineData("sideways", DrawerMode.Rail)]
    public void Parses_the_watch_page_drawer_choice(string value, DrawerMode expected)
    {
        var preferences = Create(
            $$"""{"v":1,"drawer":"expanded","watch":"{{value}}","theater":false,"stats":false,"details":true,"collapsed":[]}""");

        Assert.Equal(expected, preferences.WatchDrawer);
        Assert.Equal(DrawerMode.Expanded, preferences.Drawer);
    }

    [Fact]
    public void Each_page_kind_remembers_its_own_drawer_choice()
    {
        var preferences = Create();
        var changes = 0;
        preferences.Changed += () => changes++;

        preferences.ToggleDrawer(watching: true);
        Assert.Equal(DrawerMode.Expanded, preferences.WatchDrawer);
        Assert.Equal(DrawerMode.Expanded, preferences.Drawer);

        preferences.ToggleDrawer(watching: false);
        Assert.Equal(DrawerMode.Rail, preferences.Drawer);
        Assert.Equal(DrawerMode.Expanded, preferences.WatchDrawer);

        preferences.SetDrawer(DrawerMode.Rail, watching: true);
        preferences.SetDrawer(DrawerMode.Rail, watching: true);
        Assert.Equal(DrawerMode.Rail, preferences.DrawerFor(watching: true));
        Assert.Equal(3, changes);
    }

    [Fact]
    public async Task Persist_writes_the_watch_page_choice()
    {
        var js = new RecordingJSRuntime();
        var preferences = Create(js: js);
        preferences.SetDrawer(DrawerMode.Expanded, watching: true);

        await preferences.PersistAsync();

        var state = Assert.IsType<UiPreferencesState>(Assert.Single(Assert.Single(js.Invocations).Arguments));
        Assert.Equal("expanded", state.Watch);
        Assert.Equal("expanded", state.Drawer);
    }

    [Fact]
    public void Prerender_state_restores_the_watch_page_choice()
    {
        var prerenderStore = new TestClientStateStore();
        prerenderStore.Cookies[UiPreferences.CookieName] =
            """{"v":1,"drawer":"rail","watch":"expanded","theater":false,"stats":false,"details":true,"collapsed":[]}""";

        _ = Create(store: prerenderStore);
        var circuitStore = new TestClientStateStore();
        circuitStore.Persist(
            UiPreferences.CookieName,
            Assert.IsType<string>(prerenderStore.Persisted[UiPreferences.CookieName]));
        var preferences = Create(store: circuitStore);

        Assert.Equal(DrawerMode.Rail, preferences.Drawer);
        Assert.Equal(DrawerMode.Expanded, preferences.WatchDrawer);
    }

    private static UiPreferences Create(
        string? cookie = null,
        TestClientStateStore? store = null,
        IJSRuntime? js = null)
    {
        store ??= new TestClientStateStore();
        if (cookie is not null)
            store.Cookies[UiPreferences.CookieName] = cookie;

        return new UiPreferences(
            store,
            js ?? new RecordingJSRuntime(),
            NullLogger<UiPreferences>.Instance);
    }

    private sealed class RecordingJSRuntime : IJSRuntime
    {
        public List<(string Identifier, object?[] Arguments)> Invocations { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Invocations.Add((identifier, args ?? []));
            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class ThrowingJSRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new JSException("The browser module is unavailable.");

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            throw new JSException("The browser module is unavailable.");
    }
}
