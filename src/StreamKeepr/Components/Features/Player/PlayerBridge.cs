using Microsoft.Extensions.Logging.Abstractions;

namespace StreamKeepr.Components.Features.Player;

/// <summary>
/// Scoped seek seam between sibling UI (the details card's chapters) and the JS-owned
/// player. The player attaches its callback after the JS module initializes. Until then
/// only the latest request is retained, so a burst of chapter clicks resolves to the
/// last position rather than replaying stale ones.
/// </summary>
public sealed class PlayerBridge
{
    private readonly ILogger<PlayerBridge> _logger;
    private Func<double, ValueTask>? _seek;
    private double? _pending;

    public PlayerBridge(ILogger<PlayerBridge>? logger = null) =>
        _logger = logger ?? NullLogger<PlayerBridge>.Instance;

    /// <summary>True once the player has attached its seek callback.</summary>
    public bool IsReady => _seek is not null;

    /// <summary>
    /// Attaches the player's seek callback and flushes the latest queued request, if any.
    /// The callback owns its error handling; the bridge only observes the returned task.
    /// </summary>
    public void Attach(Func<double, ValueTask> seek)
    {
        ArgumentNullException.ThrowIfNull(seek);

        _seek = seek;
        if (_pending is not { } pending)
            return;

        _pending = null;
        _ = FlushAsync(seek, pending);
    }

    /// <summary>Releases the callback and discards an undelivered request.</summary>
    public void Detach()
    {
        _seek = null;
        _pending = null;
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

        _pending = clamped;
        return ValueTask.CompletedTask;
    }

    private async Task FlushAsync(Func<double, ValueTask> seek, double seconds)
    {
        try
        {
            await seek(seconds);
        }
        catch (Exception ex)
        {
            // The circuit may be gone between attach and flush. The next attach starts
            // from a fresh queue; there is no UI left to report this best-effort seek.
            _logger.LogDebug(ex, "A queued player seek could not be delivered.");
        }
    }
}
