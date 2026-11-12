using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Ui;

public enum ChannelRuntimeState { Recording, Reconnecting, Watching, Paused, CoolingDown }

/// <summary>One watched channel as the Settings page shows it.</summary>
public sealed record ChannelView(
    string Login,
    string DisplayName,
    string? AvatarUrl,
    bool IsWatched,
    bool IsPaused,
    ChannelRuntimeState State,
    bool PushConnected,
    string PushReason);

/// <summary>
/// The circuit's channel-management surface (backend-for-UI contract §3). Every member throws
/// <see cref="UnauthorizedAccessException"/> without a signed-in admin; the UI hides the page,
/// but this is the guard.
/// </summary>
public interface IChannelsView : IDisposable
{
    event Action? Changed;

    IReadOnlyList<ChannelView> All { get; }

    /// <summary>Validates the login through Helix and starts watching it.</summary>
    Task AddAsync(string login, CancellationToken ct);

    /// <summary>Finalizes any open session and stops watching; recordings are kept.</summary>
    Task RemoveAsync(string login, CancellationToken ct);

    /// <summary>Finalizes any open capture and parks the channel in Paused.</summary>
    Task StopAsync(string login, CancellationToken ct);

    /// <summary>Clears Paused and nudges the poller.</summary>
    Task ResumeAsync(string login, CancellationToken ct);
}

public sealed class ChannelsView : IChannelsView
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(500);

    private readonly ChannelStore _channels;
    private readonly DvrStatus _status;
    private readonly RecordingLibrary _library;
    private readonly IChannelControl _control;
    private readonly PollSchedule _schedule;
    private readonly AuthenticationStateProvider _auth;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    private volatile Task<AuthenticationState> _authState;
    private ITimer? _timer;
    private bool _disposed;

    public ChannelsView(
        ChannelStore channels,
        DvrStatus status,
        RecordingLibrary library,
        IChannelControl control,
        PollSchedule schedule,
        AuthenticationStateProvider auth,
        TimeProvider clock)
    {
        _channels = channels;
        _status = status;
        _library = library;
        _control = control;
        _schedule = schedule;
        _auth = auth;
        _clock = clock;

        _authState = auth.GetAuthenticationStateAsync();
        _auth.AuthenticationStateChanged += OnAuthenticationStateChanged;
        _channels.Changed += OnSourceChanged;
        _status.Changed += OnSourceChanged;
        _library.Changed += OnSourceChanged;
        _schedule.Changed += OnSourceChanged;
    }

    public event Action? Changed;

    public IReadOnlyList<ChannelView> All
    {
        get
        {
            RequireAdmin();
            IReadOnlyDictionary<string, DvrSnapshot> status = _status.Current;
            PushStatus push = _schedule.Push;
            var openSessions = _library.All
                .Where(recording => recording.IsRecording)
                .Select(recording => recording.Channel)
                .ToHashSet(StringComparer.Ordinal);

            return _channels.Snapshot
                .Select(entry => Build(
                    entry, status.GetValueOrDefault(entry.Login), openSessions, push))
                .OrderBy(view => view.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(view => view.Login, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public Task AddAsync(string login, CancellationToken ct)
    {
        RequireAdmin();
        return _control.AddChannelAsync(login, ct);
    }

    public Task RemoveAsync(string login, CancellationToken ct)
    {
        RequireAdmin();
        return _control.RemoveChannelAsync(login, ct);
    }

    public Task StopAsync(string login, CancellationToken ct)
    {
        RequireAdmin();
        return _control.StopRecordingAsync(login, ct);
    }

    public Task ResumeAsync(string login, CancellationToken ct)
    {
        RequireAdmin();
        return _control.ResumeRecordingAsync(login, ct);
    }

    internal static ChannelRuntimeState StateOf(
        ChannelEntry entry, DvrSnapshot? snapshot, bool hasOpenSession) =>
        entry.Paused || snapshot?.State is DvrState.Paused
            ? ChannelRuntimeState.Paused
            : snapshot?.ActiveJob is not null && snapshot.State is DvrState.Recording
                ? ChannelRuntimeState.Recording
                : snapshot?.State is DvrState.CoolingDown
                    ? ChannelRuntimeState.CoolingDown
                    : hasOpenSession
                        ? ChannelRuntimeState.Reconnecting
                        : ChannelRuntimeState.Watching;

    private static ChannelView Build(
        ChannelEntry entry,
        DvrSnapshot? snapshot,
        HashSet<string> openSessions,
        PushStatus push) =>
        new(
            entry.Login,
            string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Login : entry.DisplayName,
            entry.AvatarUrl,
            IsWatched: true,
            entry.Paused,
            StateOf(entry, snapshot, openSessions.Contains(entry.Login)),
            push.Connected,
            push.Reason);

    private void RequireAdmin()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Task<AuthenticationState> state = _authState;
        ClaimsPrincipal? user = state.IsCompletedSuccessfully ? state.Result.User : null;
        if (user?.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("Connect Twitch to manage channels.");
    }

    private void OnAuthenticationStateChanged(Task<AuthenticationState> state)
    {
        _authState = state;
        Changed?.Invoke();
    }

    private void OnSourceChanged()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null)
                return;

            _timer = _clock.CreateTimer(_ => Flush(), null, Throttle, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed)
                return;
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        _auth.AuthenticationStateChanged -= OnAuthenticationStateChanged;
        _channels.Changed -= OnSourceChanged;
        _status.Changed -= OnSourceChanged;
        _library.Changed -= OnSourceChanged;
        _schedule.Changed -= OnSourceChanged;
    }
}
