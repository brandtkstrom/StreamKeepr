using StreamKeepr.Domain;

namespace StreamKeepr.Infrastructure.Runtime;

/// <summary>
/// An immutable read-only view of one channel. Telemetry lives here, not in the reducer.
/// </summary>
public sealed record DvrSnapshot(
    DvrState State,
    DateTimeOffset LastEventAt,
    StreamObservation? LastObservation,
    JobId? ActiveJob,
    long PartBytes,
    TimeSpan PartMediaTime,
    DateTimeOffset? LastProgressAt,
    string? LastError,
    string? LastDetail);

/// <summary>
/// Thread-safe snapshots for the UI and health checks. <see cref="Current"/> returns a copy, so a
/// caller can never observe a half-applied transition or mutate runtime state.
/// </summary>
public sealed class DvrStatus
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DvrSnapshot> _channels = new(StringComparer.Ordinal);

    /// <summary>Raised on every apply, progress update, failure, and removal.</summary>
    public event Action? Changed;

    public IReadOnlyDictionary<string, DvrSnapshot> Current
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, DvrSnapshot>(_channels, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Records the result of applying an event. A job change resets part telemetry;
    /// <see cref="DvrSnapshot.LastDetail"/> records every exit reason while
    /// <see cref="DvrSnapshot.LastError"/> is reserved for failures and kills.
    /// </summary>
    public void Applied(string channel, DvrEvent? @event, DvrState next, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(next);

        lock (_gate)
        {
            DvrSnapshot current = _channels.TryGetValue(channel, out DvrSnapshot? existing)
                ? existing
                : new DvrSnapshot(
                    new DvrState.Idle(), at, null, null, 0, TimeSpan.Zero, null, null, null);

            JobId? active = next is DvrState.Recording recording ? recording.Job : null;
            bool jobChanged = active != current.ActiveJob;
            string? lastDetail = current.LastDetail;
            string? lastError = current.LastError;

            if (@event is DvrEvent.RecorderExited exit)
            {
                lastDetail = exit.Detail;
                if (exit.Kind == RecorderExitKind.Killed)
                    lastError = exit.Detail;
            }

            _channels[channel] = current with
            {
                State = next,
                LastEventAt = at,
                LastObservation = @event is DvrEvent.StreamStatusObserved observed
                    ? observed.Observation
                    : current.LastObservation,
                ActiveJob = active,
                // A new part starts with clean telemetry. A late callback from the old job is
                // ignored by ReportProgress because its JobId no longer matches.
                PartBytes = jobChanged ? 0 : current.PartBytes,
                PartMediaTime = jobChanged ? TimeSpan.Zero : current.PartMediaTime,
                LastProgressAt = jobChanged ? null : current.LastProgressAt,
                LastError = lastError,
                LastDetail = lastDetail,
            };
        }

        Changed?.Invoke();
    }

    /// <summary>Updates telemetry only for the active job; stale job callbacks are ignored.</summary>
    public void ReportProgress(
        string channel, JobId job, long bytes, TimeSpan mediaTime, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(mediaTime, TimeSpan.Zero);

        lock (_gate)
        {
            if (!_channels.TryGetValue(channel, out DvrSnapshot? current) || current.ActiveJob != job)
                return;

            _channels[channel] = current with
            {
                PartBytes = bytes,
                PartMediaTime = mediaTime,
                LastProgressAt = at,
            };
        }

        Changed?.Invoke();
    }

    /// <summary>Surfaces a command or recorder failure without changing the state.</summary>
    public void Failed(string channel, string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(error);

        lock (_gate)
        {
            if (!_channels.TryGetValue(channel, out DvrSnapshot? current))
                return;

            _channels[channel] = current with { LastError = error };
        }

        Changed?.Invoke();
    }

    /// <summary>Drops a removed channel's snapshot and raises <see cref="Changed"/>.</summary>
    public void Remove(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);

        bool removed;
        lock (_gate)
            removed = _channels.Remove(channel);

        if (removed)
            Changed?.Invoke();
    }
}
