using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Refreshes the in-memory live playlist snapshots off the request path. Each live session is
/// refreshed at most once per tick; a busy session skips the tick instead of queueing. Failures
/// are isolated per session, backed off, and never replace the last-known-good body.
/// </summary>
public sealed class LivePlaylistService(
    RecordingLibrary library,
    LivePlaylistCache cache,
    ILivePlaylistIo io,
    IOptions<LivePlaylistOptions> options,
    TimeProvider clock,
    ILogger<LivePlaylistService> logger) : BackgroundService
{
    private const double MaxBackoffSeconds = 30;

    private readonly ConcurrentDictionary<string, SessionState> _sessions =
        new(StringComparer.Ordinal);

    private readonly TimeSpan _refreshInterval =
        TimeSpan.FromSeconds(options.Value.RefreshSeconds);

    private readonly TimeSpan _refreshTimeout =
        TimeSpan.FromSeconds(options.Value.RefreshTimeoutSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_refreshInterval, clock);
        try
        {
            // StartAsync must return promptly; the immediate refresh runs on this loop.
            await Task.Yield();
            await RefreshAllAsync(stoppingToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await RefreshAllAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    /// <summary>
    /// Prunes snapshots for sessions that are no longer recording or no longer in the library,
    /// then refreshes every remaining live session. Per-session failures never escape.
    /// </summary>
    public async Task RefreshAllAsync(CancellationToken ct)
    {
        if (!library.IsLoaded)
            return;

        RecordingSummary[] live = library.All
            .Where(summary => summary.IsRecording)
            .ToArray();
        var liveIds = live.Select(summary => summary.Id).ToHashSet(StringComparer.Ordinal);

        foreach (string id in cache.Ids)
        {
            if (!liveIds.Contains(id))
                Invalidate(id);
        }

        await Task.WhenAll(live.Select(summary => RefreshOneAsync(summary, ct)))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes one live session. Returns immediately when the session's gate is busy or its
    /// previous failure is still backing off.
    /// </summary>
    public async Task<string?> RefreshOneAsync(RecordingSummary summary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (!summary.IsRecording)
        {
            Invalidate(summary.Id);
            return null;
        }

        SessionState state = _sessions.GetOrAdd(summary.Id, static _ => new SessionState());
        if (!await state.Gate.WaitAsync(0, ct).ConfigureAwait(false))
            return cache.Find(summary.Id)?.Body;

        try
        {
            DateTimeOffset now = clock.GetUtcNow();
            if (state.NextAttemptUtcTicks > now.UtcTicks)
                return cache.Find(summary.Id)?.Body;

            using var timeout = new CancellationTokenSource(_refreshTimeout, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                LivePlaylistRead read = await io.ReadAsync(summary, linked.Token)
                    .ConfigureAwait(false);
                if (read.Kind == LivePlaylistKind.Complete)
                {
                    Invalidate(summary.Id);
                    state.Succeed();
                    return read.Body;
                }

                if (read.Kind != LivePlaylistKind.Live || read.Body is null)
                {
                    Invalidate(summary.Id);
                    state.Succeed();
                    return null;
                }

                // Finalize may have completed while this refresh was in flight. The library
                // is the authority: never publish a live body for a session that is no longer
                // Recording. The endpoint already serves the finalized index.
                if (library.Find(summary.Id) is not { IsRecording: true })
                {
                    Invalidate(summary.Id);
                    state.Succeed();
                    return null;
                }

                LivePlaylistSnapshot? current = cache.Find(summary.Id);
                if (current is not null
                    && string.Equals(current.Body, read.Body, StringComparison.Ordinal))
                {
                    state.Succeed();
                    return current.Body;
                }

                cache.Publish(LivePlaylistSnapshot.Create(
                    summary.Id,
                    state.NextVersion(),
                    read.Body,
                    PlaylistTimeline.Parse(read.Body).SegmentCount,
                    clock.GetUtcNow()));
                state.Succeed();
                return read.Body;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                Fail(state, summary, now, ex, "timed out");
                return cache.Find(summary.Id)?.Body;
            }
            catch (Exception ex) when (IsRefreshFailure(ex))
            {
                Fail(state, summary, now, ex, "failed");
                return cache.Find(summary.Id)?.Body;
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>
    /// The thumbnail path: a live recording refreshes through the same single-flight gate and
    /// returns the current snapshot (possibly last-known-good); a complete recording reads the
    /// finalized index once and is not cached.
    /// </summary>
    public async Task<string?> GetOrRefreshAsync(RecordingSummary summary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (!summary.IsRecording)
        {
            LivePlaylistRead read = await io.ReadAsync(summary, ct).ConfigureAwait(false);
            return read.Kind == LivePlaylistKind.Complete ? read.Body : null;
        }

        return await RefreshOneAsync(summary, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops the service snapshot and releases any IO-side parsed parts for a session that is no
    /// longer live. Safe to call more than once.
    /// </summary>
    private void Invalidate(string id)
    {
        cache.Remove(id);
        if (io is ILivePlaylistSnapshotInvalidator invalidator)
            invalidator.Remove(id);
    }

    private void Fail(
        SessionState state,
        RecordingSummary summary,
        DateTimeOffset now,
        Exception exception,
        string outcome)
    {
        (int failures, TimeSpan delay) = state.Fail(now, _refreshInterval);

        // One Warning per failure window keeps a failing disk visible without flooding the log;
        // later failures in the same window are detail for diagnostics.
        LogLevel level = failures == 1 ? LogLevel.Warning : LogLevel.Debug;
        logger.Log(
            level,
            exception,
            "Live playlist refresh {Outcome} for session {SessionId} (failure {FailureCount} in this window); keeping the last-known-good body and retrying in {RetrySeconds:0.#} s.",
            outcome,
            summary.Id,
            failures,
            delay.TotalSeconds);
    }

    private static bool IsRefreshFailure(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or TimeoutException
            or InvalidDataException
            or NotSupportedException
            or FormatException
            or System.Text.Json.JsonException;

    private sealed class SessionState
    {
        private long _version;
        private int _failures;
        private long _nextAttemptUtcTicks;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public long NextAttemptUtcTicks => Volatile.Read(ref _nextAttemptUtcTicks);

        // Only the gate holder calls NextVersion, Succeed, and Fail.
        public long NextVersion() => Interlocked.Increment(ref _version);

        public void Succeed()
        {
            Interlocked.Exchange(ref _failures, 0);
            Volatile.Write(ref _nextAttemptUtcTicks, 0);
        }

        public (int Failures, TimeSpan Delay) Fail(DateTimeOffset now, TimeSpan baseDelay)
        {
            int failures = Interlocked.Increment(ref _failures);
            double seconds = baseDelay.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 5));
            TimeSpan delay = TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoffSeconds));
            Volatile.Write(ref _nextAttemptUtcTicks, now.Add(delay).UtcTicks);
            return (failures, delay);
        }
    }
}
