using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Ui;

/// <summary>
/// Scoped per-viewer layout preferences. The versioned <c>sk-ui</c> cookie is
/// read while prerendering, restored into the circuit through
/// <see cref="IClientStateStore"/>, and written back by <c>prefs.js</c>.
/// </summary>
public sealed class UiPreferences
{
    public const string CookieName = "sk-ui";
    public const int CurrentVersion = 1;
    public const int MaxCookieBytes = 4096;
    public const int MaxCollapsedStreamers = 50;

    private const string PersistenceKey = "sk-ui";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IClientStateStore _store;
    private readonly IJSRuntime _js;
    private readonly ILogger<UiPreferences> _logger;
    private readonly List<string> _collapsed = [];

    private DrawerMode _drawer = DrawerMode.Expanded;
    private DrawerMode _watchDrawer = DrawerMode.Rail;
    private bool _theater;
    private bool _statsOpen;
    private bool _detailsOpen = true;
    private bool _chatOpen;
    private double _chatSync;

    public UiPreferences(IClientStateStore store, IJSRuntime js, ILogger<UiPreferences> logger)
    {
        _store = store;
        _js = js;
        _logger = logger;

        var cookie = store.ReadCookie(CookieName);
        if (cookie is not null)
        {
            Apply(cookie);
        }
        else if (store.TryRestore<string>(PersistenceKey, out var persisted) &&
                 !string.IsNullOrWhiteSpace(persisted))
        {
            Apply(persisted);
        }

        // Capture the prerender result for hydration; this is a no-op once the
        // circuit has started.
        store.Persist(PersistenceKey, ToJson());
    }

    /// <summary>The drawer on Home and other non-watch pages; the full library by default.</summary>
    public DrawerMode Drawer => _drawer;

    /// <summary>The drawer on a watch page (<c>/r/{id}</c>); the rail by default, so the player gets the room.</summary>
    public DrawerMode WatchDrawer => _watchDrawer;

    /// <summary>The remembered drawer choice for the kind of page being shown.</summary>
    public DrawerMode DrawerFor(bool watching) => watching ? _watchDrawer : _drawer;

    public bool Theater => _theater;

    public bool StatsOpen => _statsOpen;

    public bool DetailsOpen => _detailsOpen;

    public bool ChatOpen => _chatOpen;

    /// <summary>The viewer's chat sync offset in seconds, the same for every recording.
    /// Positive shows chat later. Always within the chat endpoint's limit, in 0.5 s steps.</summary>
    public double ChatSyncOffset => _chatSync;

    public IReadOnlyList<string> CollapsedStreamers => _collapsed.ToArray();

    public event Action? Changed;

    public bool IsCollapsed(string streamerLogin) =>
        !string.IsNullOrWhiteSpace(streamerLogin) &&
        _collapsed.Contains(streamerLogin.Trim(), StringComparer.OrdinalIgnoreCase);

    public void SetDrawer(DrawerMode mode) => SetDrawer(mode, watching: false);

    /// <summary>Sets the drawer choice for one kind of page; the other kind keeps its own.</summary>
    public void SetDrawer(DrawerMode mode, bool watching)
    {
        if (mode == DrawerFor(watching))
            return;

        if (watching)
            _watchDrawer = mode;
        else
            _drawer = mode;
        OnChanged();
    }

    /// <summary>The app-bar menu toggles the expanded and rail modes.</summary>
    public void ToggleDrawer() => ToggleDrawer(watching: false);

    /// <summary>Toggles the expanded and rail modes for one kind of page.</summary>
    public void ToggleDrawer(bool watching) =>
        SetDrawer(
            DrawerFor(watching) == DrawerMode.Expanded ? DrawerMode.Rail : DrawerMode.Expanded,
            watching);

    public void SetTheater(bool theater)
    {
        if (theater == _theater)
            return;

        _theater = theater;
        OnChanged();
    }

    public void SetStatsOpen(bool open)
    {
        if (open == _statsOpen)
            return;

        _statsOpen = open;
        OnChanged();
    }

    public void SetDetailsOpen(bool open)
    {
        if (open == _detailsOpen)
            return;

        _detailsOpen = open;
        OnChanged();
    }

    public void SetChatOpen(bool open)
    {
        if (open == _chatOpen)
            return;

        _chatOpen = open;
        OnChanged();
    }

    /// <summary>Sets the chat sync offset, clamped to the endpoint's limit and snapped to
    /// 0.5 s. A non-finite value is ignored.</summary>
    public void SetChatSyncOffset(double seconds)
    {
        if (!double.IsFinite(seconds))
            return;

        var value = SnapChatSync(seconds);
        if (value == _chatSync)
            return;

        _chatSync = value;
        OnChanged();
    }

    public void SetCollapsed(string streamerLogin, bool collapsed)
    {
        if (string.IsNullOrWhiteSpace(streamerLogin))
            return;

        var login = streamerLogin.Trim();
        var index = _collapsed.FindIndex(item => string.Equals(item, login, StringComparison.OrdinalIgnoreCase));
        if (collapsed == (index >= 0))
            return;

        if (collapsed)
        {
            _collapsed.Add(login);
            TrimCollapsed();
        }
        else
        {
            _collapsed.RemoveAt(index);
        }

        OnChanged();
    }

    /// <summary>Persists the current state through the browser module.</summary>
    public async Task PersistAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("skPrefs.writeUi", CreateState());
        }
        catch (JSException ex)
        {
            // prefs.js may not have loaded yet, and a closed circuit cannot write.
            _logger.LogDebug(ex, "UI preferences were not written to the browser.");
        }
        catch (InvalidOperationException ex)
        {
            // JavaScript interop is unavailable while prerendering.
            _logger.LogDebug(ex, "UI preferences were not written during prerendering.");
        }
    }

    private void OnChanged() => Changed?.Invoke();

    private void Apply(string json)
    {
        CookieState? state;
        try
        {
            state = JsonSerializer.Deserialize<CookieState>(Decode(json), JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }
        catch (UriFormatException)
        {
            return;
        }

        if (state is null || state.Version != CurrentVersion)
            return;

        _drawer = ParseDrawer(state.Drawer, DrawerMode.Expanded);
        _watchDrawer = ParseDrawer(state.Watch, DrawerMode.Rail);
        _theater = state.Theater ?? false;
        _statsOpen = state.Stats ?? false;
        _detailsOpen = state.Details ?? true;
        _chatOpen = state.Chat ?? false;
        _chatSync = state.ChatSync is { } sync && double.IsFinite(sync) ? SnapChatSync(sync) : 0;

        _collapsed.Clear();
        if (state.Collapsed is not null)
        {
            foreach (var login in state.Collapsed)
            {
                if (string.IsNullOrWhiteSpace(login))
                    continue;

                AddCollapsed(login.Trim());
            }
        }
    }

    private static double SnapChatSync(double seconds)
    {
        var limit = ChatReader.MaxSyncOffsetSeconds;
        var snapped = Math.Round(Math.Clamp(seconds, -limit, limit) * 2, MidpointRounding.AwayFromZero) / 2;
        // Never store -0, which would serialize as "-0".
        return snapped == 0 ? 0 : snapped;
    }

    private static DrawerMode ParseDrawer(string? value, DrawerMode fallback) => value switch
    {
        "expanded" => DrawerMode.Expanded,
        "rail" => DrawerMode.Rail,
        "hidden" => DrawerMode.Hidden,
        _ => fallback,
    };

    private static string FormatDrawer(DrawerMode mode) => mode switch
    {
        DrawerMode.Rail => "rail",
        DrawerMode.Hidden => "hidden",
        _ => "expanded",
    };

    private static string Decode(string value)
    {
        // ASP.NET Core normally decodes request cookies, but accept manually
        // encoded values too so a prefs.js-written cookie always parses.
        return value.StartsWith('%') ? Uri.UnescapeDataString(value) : value;
    }

    private void AddCollapsed(string login)
    {
        if (_collapsed.Contains(login, StringComparer.OrdinalIgnoreCase))
            return;

        _collapsed.Add(login);
        TrimCollapsed();
    }

    private void TrimCollapsed()
    {
        while (_collapsed.Count > MaxCollapsedStreamers)
            _collapsed.RemoveAt(0);

        while (_collapsed.Count > 0 && Encoding.UTF8.GetByteCount(Serialize(CreateState())) > MaxCookieBytes)
            _collapsed.RemoveAt(0);
    }

    private UiPreferencesState CreateState() => new(
        CurrentVersion,
        FormatDrawer(_drawer),
        FormatDrawer(_watchDrawer),
        _theater,
        _statsOpen,
        _detailsOpen,
        _chatOpen,
        _collapsed.ToArray(),
        _chatSync);

    private string ToJson() => Serialize(CreateState());

    private static string Serialize(UiPreferencesState state) =>
        JsonSerializer.Serialize(state, JsonOptions);

    private sealed class CookieState
    {
        [JsonPropertyName("v")] public int Version { get; set; }

        [JsonPropertyName("drawer")] public string? Drawer { get; set; }

        [JsonPropertyName("watch")] public string? Watch { get; set; }

        [JsonPropertyName("theater")] public bool? Theater { get; set; }

        [JsonPropertyName("stats")] public bool? Stats { get; set; }

        [JsonPropertyName("details")] public bool? Details { get; set; }

        [JsonPropertyName("chat")] public bool? Chat { get; set; }

        [JsonPropertyName("chatSync")] public double? ChatSync { get; set; }

        [JsonPropertyName("collapsed")] public List<string?>? Collapsed { get; set; }
    }
}

/// <summary>The JSON shape shared by the cookie, prefs.js, and persisted state.</summary>
public sealed record UiPreferencesState(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("drawer")] string Drawer,
    [property: JsonPropertyName("watch")] string Watch,
    [property: JsonPropertyName("theater")] bool Theater,
    [property: JsonPropertyName("stats")] bool Stats,
    [property: JsonPropertyName("details")] bool Details,
    [property: JsonPropertyName("chat")] bool Chat,
    [property: JsonPropertyName("collapsed")] IReadOnlyList<string> Collapsed,
    [property: JsonPropertyName("chatSync")] double ChatSync);
