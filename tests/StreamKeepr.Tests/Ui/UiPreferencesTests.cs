using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class UiPreferencesTests
{
    [Fact]
    public void Missing_cookie_uses_defaults()
    {
        var preferences = Create();

        Assert.False(preferences.Theater);
        Assert.False(preferences.StatsOpen);
        Assert.True(preferences.DetailsOpen);
        Assert.True(preferences.ViewersOpen);
        Assert.False(preferences.ChatOpen);
    }

    [Fact]
    public void Parses_the_versioned_cookie()
    {
        var preferences = Create(
            """{"v":1,"theater":true,"stats":true,"details":false,"viewers":false,"chat":true}""");

        Assert.True(preferences.Theater);
        Assert.True(preferences.StatsOpen);
        Assert.False(preferences.DetailsOpen);
        Assert.False(preferences.ViewersOpen);
        Assert.True(preferences.ChatOpen);
    }

    [Fact]
    public void Encoded_cookie_is_decoded()
    {
        var json = """{"v":1,"theater":true,"stats":false,"details":true}""";
        var preferences = Create(Uri.EscapeDataString(json));

        Assert.True(preferences.Theater);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"v":2,"theater":true}""")]
    [InlineData("""{"theater":true}""")]
    public void Malformed_or_unknown_version_falls_back_to_defaults(string cookie)
    {
        var preferences = Create(cookie);

        Assert.False(preferences.Theater);
        Assert.True(preferences.DetailsOpen);
    }

    [Fact]
    public void Unknown_fields_invalid_values_and_an_older_versions_keys_are_ignored()
    {
        // drawer, watch and collapsed are what the tree library and its remembered drawer wrote.
        var preferences = Create(
            """{"v":1,"drawer":"sideways","watch":"expanded","theater":null,"stats":true,"details":false,"extra":"ignored","collapsed":[null,"","pixelwren"]}""");

        Assert.False(preferences.Theater);
        Assert.True(preferences.StatsOpen);
        Assert.False(preferences.DetailsOpen);
    }

    [Fact]
    public void Setters_raise_changed_only_when_state_changes()
    {
        var preferences = Create();
        var changes = 0;
        preferences.Changed += () => changes++;

        preferences.SetTheater(true);
        preferences.SetTheater(true);
        preferences.SetStatsOpen(true);
        preferences.SetDetailsOpen(false);
        preferences.SetViewersOpen(false);
        preferences.SetViewersOpen(false);
        preferences.SetChatOpen(true);

        Assert.Equal(5, changes);
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
        var preferences = Create(
            """{"v":1,"drawer":"rail","collapsed":["pixelwren"]}""", js: js);
        preferences.SetTheater(true);

        await preferences.PersistAsync();

        var invocation = Assert.Single(js.Invocations);
        Assert.Equal("skPrefs.writeUi", invocation.Identifier);
        var state = Assert.IsType<UiPreferencesState>(Assert.Single(invocation.Arguments));
        Assert.Equal(1, state.Version);
        Assert.True(state.Theater);
        Assert.True(state.Details);
        // An older version's keys are not written back.
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(state));
        Assert.Equal(
            ["v", "theater", "stats", "details", "chat", "chatSync", "viewers"],
            json.RootElement.EnumerateObject().Select(property => property.Name));
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
            """{"v":1,"theater":true,"stats":true,"details":false,"viewers":false}""";

        _ = Create(store: prerenderStore);
        var persisted = Assert.IsType<string>(prerenderStore.Persisted[UiPreferences.CookieName]);

        var circuitStore = new TestClientStateStore();
        circuitStore.Persist(UiPreferences.CookieName, persisted);
        var preferences = Create(store: circuitStore);

        Assert.True(preferences.Theater);
        Assert.True(preferences.StatsOpen);
        Assert.False(preferences.DetailsOpen);
        Assert.False(preferences.ViewersOpen);
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
