using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Ui;

/// <summary>
/// Scoped per-viewer layout preferences. The versioned <c>sk-ui</c> cookie is
/// read while prerendering, restored into the circuit through
/// <see cref="IClientStateStore"/>, and written back by <c>prefs.js</c>. Keys an older
/// version wrote (<c>drawer</c>, <c>watch</c>, <c>collapsed</c>) are read and ignored: the
/// library is a temporary overlay that is never remembered, and it has no groups to collapse.
/// </summary>
public sealed class UiPreferences
{
    public const string CookieName = "sk-ui";
    public const int CurrentVersion = 1;

    /// <summary>Chat sync moves and is stored in steps of this many seconds (the Settings slider's step).</summary>
    public const double ChatSyncStepSeconds = 0.5;

    private const string PersistenceKey = "sk-ui";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IClientStateStore _store;
    private readonly IJSRuntime _js;
    private readonly ILogger<UiPreferences> _logger;

    private bool _theater;
    private bool _statsOpen;
    private bool _detailsOpen = true;
    private bool _viewersOpen = true;
    private bool _chatOpen;
    private double _chatSync;
    private bool _chatShowBadges = true;

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

    public bool Theater => _theater;

    public bool StatsOpen => _statsOpen;

    public bool DetailsOpen => _detailsOpen;

    /// <summary>The Viewers panel under the title block; open by default.</summary>
    public bool ViewersOpen => _viewersOpen;

    public bool ChatOpen => _chatOpen;

    /// <summary>The viewer's chat sync offset in seconds, the same for every recording.
    /// Positive shows chat later. Always within the chat endpoint's limit, in 0.5 s steps.</summary>
    public double ChatSyncOffset => _chatSync;

    /// <summary>Whether chat rows show badges, as catalog images or their text-chip fallback;
    /// on by default.</summary>
    public bool ChatShowBadges => _chatShowBadges;

    public event Action? Changed;

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

    public void SetViewersOpen(bool open)
    {
        if (open == _viewersOpen)
            return;

        _viewersOpen = open;
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

    /// <summary>Shows or hides badges, images and text chips, in every chat pane.</summary>
    public void SetChatShowBadges(bool show)
    {
        if (show == _chatShowBadges)
            return;

        _chatShowBadges = show;
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

        _theater = state.Theater ?? false;
        _statsOpen = state.Stats ?? false;
        _detailsOpen = state.Details ?? true;
        _viewersOpen = state.Viewers ?? true;
        _chatOpen = state.Chat ?? false;
        _chatSync = state.ChatSync is { } sync && double.IsFinite(sync) ? SnapChatSync(sync) : 0;
        _chatShowBadges = state.ChatShowBadges ?? true;
    }

    private static double SnapChatSync(double seconds)
    {
        var limit = ChatReader.MaxSyncOffsetSeconds;
        var snapped = Math.Round(
            Math.Clamp(seconds, -limit, limit) / ChatSyncStepSeconds, MidpointRounding.AwayFromZero) * ChatSyncStepSeconds;
        // Never store -0, which would serialize as "-0".
        return snapped == 0 ? 0 : snapped;
    }

    private static string Decode(string value)
    {
        // ASP.NET Core normally decodes request cookies, but accept manually
        // encoded values too so a prefs.js-written cookie always parses.
        return value.StartsWith('%') ? Uri.UnescapeDataString(value) : value;
    }

    private UiPreferencesState CreateState() => new(
        CurrentVersion,
        _theater,
        _statsOpen,
        _detailsOpen,
        _chatOpen,
        _chatSync,
        _viewersOpen,
        _chatShowBadges);

    private string ToJson() => Serialize(CreateState());

    private static string Serialize(UiPreferencesState state) =>
        JsonSerializer.Serialize(state, JsonOptions);

    private sealed class CookieState
    {
        [JsonPropertyName("v")] public int Version { get; set; }

        [JsonPropertyName("theater")] public bool? Theater { get; set; }

        [JsonPropertyName("stats")] public bool? Stats { get; set; }

        [JsonPropertyName("details")] public bool? Details { get; set; }

        [JsonPropertyName("chat")] public bool? Chat { get; set; }

        [JsonPropertyName("viewers")] public bool? Viewers { get; set; }

        [JsonPropertyName("chatSync")] public double? ChatSync { get; set; }

        // The wire name predates images and stays for cookie compatibility: the C# property
        // was renamed when the switch grew from text chips to all badges.
        [JsonPropertyName("chatShowBadgeText")] public bool? ChatShowBadges { get; set; }
    }
}

/// <summary>The JSON shape shared by the cookie, prefs.js, and persisted state.</summary>
public sealed record UiPreferencesState(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("theater")] bool Theater,
    [property: JsonPropertyName("stats")] bool Stats,
    [property: JsonPropertyName("details")] bool Details,
    [property: JsonPropertyName("chat")] bool Chat,
    [property: JsonPropertyName("chatSync")] double ChatSync,
    [property: JsonPropertyName("viewers")] bool Viewers = true,
    // The wire key stays `chatShowBadgeText` so version 1 cookies need no migration.
    [property: JsonPropertyName("chatShowBadgeText")] bool ChatShowBadges = true);
