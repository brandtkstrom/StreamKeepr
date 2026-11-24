using Microsoft.Extensions.Logging.Abstractions;

namespace StreamKeepr.Components.Features.Player;

/// <summary>
/// Scoped seek seam between sibling UI (the details card's chapters) and the JS-owned
/// player. The player attaches its callback after the JS module initializes. Until then
/// only the latest request is retained, so a burst of chapter clicks resolves to the
/// last position rather than replaying stale ones.
/// </summary>
/// <summary>
/// Scoped control seam between sibling UI (the details card's chapters and a library selection)
/// and the JS-owned player. The player attaches its callbacks after the JS module initializes.
/// Until then only the latest seek is retained, and a play request waits for the player, so a
/// burst of chapter clicks resolves to the last position rather than replaying stale ones.
/// </summary>
public sealed class PlayerBridge
{
    private readonly ILogger<PlayerBridge> _logger;
    private Func<double, ValueTask>? _seek;
    private Func<ValueTask>? _play;
    private double? _pendingSeek;
    private bool _pendingPlay;

    public PlayerBridge(ILogger<PlayerBridge>? logger = null) =>
        _logger = logger ?? NullLogger<PlayerBridge>.Instance;

    /// <summary>True once the player has attached its seek callback.</summary>
    public bool IsReady => _seek is not null;

    /// <summary>
    /// Attaches the player's seek and play callbacks and flushes the latest queued requests, if
    /// any. A callback owns its error handling; the bridge only observes the returned task.
    /// </summary>
    public void Attach(Func<double, ValueTask> seek, Func<ValueTask>? play = null)
    {
        ArgumentNullException.ThrowIfNull(seek);

        _seek = seek;
        _play = play;

        if (_pendingSeek is { } pendingSeek)
        {
            _pendingSeek = null;
            _ = FlushAsync(() => seek(pendingSeek), "seek");
        }

        if (_pendingPlay && play is not null)
        {
            _pendingPlay = false;
            _ = FlushAsync(play, "play");
        }
    }

    /// <summary>Releases the callbacks and discards undelivered requests.</summary>
    public void Detach()
    {
        _seek = null;
        _play = null;
        _pendingSeek = null;
        _pendingPlay = false;
    }

    /// <summary>
    /// Seeks the player, or keeps the latest request until <see cref="Attach"/> runs.
    /// Negative and non-finite values are ignored/clamped; the player clamps the upper
    /// bound to its known duration.
    /// </summary>
    public ValueTask SeekAsync(double seconds)
    {
        if (!double.IsFinite(seconds))
            return ValueTask.CompletedTask;

        double clamped = Math.Max(0, seconds);
        if (_seek is { } seek)
            return seek(clamped);

        _pendingSeek = clamped;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Asks the player to start playing without reloading the source, or keeps the request until
    /// <see cref="Attach"/> runs. Used when a library selection repeats the open recording, where
    /// the player gets no new load payload.
    /// </summary>
    public ValueTask PlayAsync()
    {
        if (_play is { } play)
            return play();

        _pendingPlay = true;
        return ValueTask.CompletedTask;
    }

    private async Task FlushAsync(Func<ValueTask> request, string kind)
    {
        try
        {
            await request();
        }
        catch (Exception ex)
        {
            // The circuit may be gone between attach and flush. The next attach starts
            // from a fresh queue; there is no UI left to report this best-effort request.
            _logger.LogDebug(ex, "A queued player {Kind} request could not be delivered.", kind);
        }
    }
}
