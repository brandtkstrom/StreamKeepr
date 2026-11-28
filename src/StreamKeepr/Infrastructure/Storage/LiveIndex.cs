using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// The live index of every Recording session, built off the request path. Each live session has
/// one entry: a single-flight refresh gate, the parsed parts the last build used, the published
/// snapshot, and the refresh backoff. Requests read the published snapshot and never wait;
/// <see cref="LiveIndexRefresher"/> refreshes every live session once per tick.
/// </summary>
/// <remarks>
/// A refresh reads the session record and part playlists without the session lock. A warm
/// refresh only stats part files; only the active last part (or the previous last part once a
/// new part appears) is read again. A failed or timed-out refresh keeps the last-known-good
/// body and backs off. When a session stops being Recording or leaves the library, its whole
/// entry is removed. A build that raced the removal finishes on the orphaned entry and is never
/// served.
/// </remarks>
public sealed class LiveIndex
{
    private const double MaxBackoffSeconds = 30;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly RecordingLibrary _library;
    private readonly SessionStore _store;
    private readonly IOptions<StorageOptions> _storage;
    private readonly IPartPlaylistSource _source;
    private readonly TimeProvider _clock;
    private readonly ILogger<LiveIndex> _logger;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeSpan _refreshTimeout;
    private readonly TimeSpan _staleAfter;

    public LiveIndex(
        RecordingLibrary library,
        SessionStore store,
        IOptions<StorageOptions> storage,
        IOptions<LivePlaylistOptions> options,
        TimeProvider clock,
        ILogger<LiveIndex> logger,
        IPartPlaylistSource source)
    {
        ArgumentNullException.ThrowIfNull(options);
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _refreshInterval = TimeSpan.FromSeconds(options.Value.RefreshSeconds);
        _refreshTimeout = TimeSpan.FromSeconds(options.Value.RefreshTimeoutSeconds);
        _staleAfter = TimeSpan.FromSeconds(options.Value.StaleAfterSeconds);
    }

    /// <summary>The interval between refresh ticks.</summary>
    internal TimeSpan RefreshInterval => _refreshInterval;

    /// <summary>Number of sessions with an entry; tests use it to prove entries are evicted.</summary>
    internal int SessionCount => _entries.Count;

    /// <summary>The published snapshot for a session, or null before its first successful refresh.</summary>
    public LiveIndexSnapshot? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _entries.TryGetValue(id, out Entry? entry) ? entry.Published : null;
    }

    /// <summary>
    /// Decides what the live index endpoint serves for <paramref name="channel"/> and
    /// <paramref name="id"/>, without waiting on a refresh or reading a playlist. The library is
    /// the authority: a Complete session serves its session index even while its live entry still
    /// exists, and a Recording session serves only its published body, however old.
    /// </summary>
    public LiveIndexLookup Lookup(string channel, string id)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(id);

        // Recovery marks the library loaded before the host accepts requests. Before that,
        // nothing may fall back to a disk scan.
        if (!_library.IsLoaded)
            return new LiveIndexLookup.Unavailable("the recording library is not loaded");

        RecordingSummary? summary = _library.Find(id);
        if (summary is null || !string.Equals(summary.Channel, channel, StringComparison.Ordinal))
            return new LiveIndexLookup.NotFound();

        if (!summary.IsRecording)
        {
            if (!TryResolveDirectory(summary, out string directory))
                return new LiveIndexLookup.NotFound();

            string indexPath = Path.Combine(directory, SessionStore.IndexFileName);
            return File.Exists(indexPath)
                ? new LiveIndexLookup.SessionIndex(indexPath)
                : new LiveIndexLookup.NotFound();
        }

        if (Find(summary.Id) is not { } snapshot)
            return new LiveIndexLookup.Unavailable("no live snapshot is cached");

        TimeSpan age = _clock.GetUtcNow() - snapshot.PublishedAt;
        return new LiveIndexLookup.Live(snapshot, age, Stale: age >= _staleAfter);
    }

    /// <summary>
    /// Removes entries for sessions that are no longer recording or no longer in the library,
    /// then refreshes every remaining live session concurrently. Per-session failures never escape.
    /// </summary>
    public async Task RefreshAllAsync(CancellationToken ct)
    {
        if (!_library.IsLoaded)
            return;

        RecordingSummary[] live = _library.All
            .Where(summary => summary.IsRecording)
            .ToArray();
        var liveIds = live.Select(summary => summary.Id).ToHashSet(StringComparer.Ordinal);

        foreach (string id in _entries.Keys)
        {
            if (!liveIds.Contains(id))
                _entries.TryRemove(id, out _);
        }

        await Task.WhenAll(live.Select(summary => RefreshOneAsync(summary, ct)))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes one live session and returns its current body. Returns immediately with the
    /// published body when the session's gate is busy or its previous failure is still backing
    /// off. Returns the session index instead when the session turns out to be Complete.
    /// </summary>
    public async Task<string?> RefreshOneAsync(RecordingSummary summary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (!summary.IsRecording)
        {
            _entries.TryRemove(summary.Id, out _);
            return null;
        }

        Entry entry = _entries.GetOrAdd(summary.Id, static _ => new Entry());
        if (!await entry.Gate.WaitAsync(0, ct).ConfigureAwait(false))
            return entry.Published?.Body;

        try
        {
            DateTimeOffset now = _clock.GetUtcNow();
            if (entry.NextAttemptUtcTicks > now.UtcTicks)
                return entry.Published?.Body;

            using var timeout = new CancellationTokenSource(_refreshTimeout, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                SessionRead read = await ReadAsync(summary, entry, linked.Token)
                    .ConfigureAwait(false);
                if (read.Kind != SessionReadKind.Live)
                {
                    Remove(summary.Id, entry);
                    entry.Succeed();
                    return read.Body;
                }

                // Finalize may have completed while this refresh was in flight. The library is
                // the authority: never publish a live body for a session that is no longer
                // Recording; the endpoint already serves the session index.
                if (_library.Find(summary.Id) is not { IsRecording: true })
                {
                    Remove(summary.Id, entry);
                    entry.Succeed();
                    return null;
                }

                string body = read.Body!;
                LiveIndexSnapshot? current = entry.Published;
                if (current is not null && string.Equals(current.Body, body, StringComparison.Ordinal))
                {
                    entry.Succeed();
                    return current.Body;
                }

                entry.Published = LiveIndexSnapshot.Create(summary.Id, body, _clock.GetUtcNow());
                entry.Succeed();
                return body;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                Fail(entry, summary, now, ex, "timed out");
                return entry.Published?.Body;
            }
            catch (Exception ex) when (IsRefreshFailure(ex))
            {
                Fail(entry, summary, now, ex, "failed");
                return entry.Published?.Body;
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    /// The thumbnail path: a live recording refreshes through the same single-flight gate and
    /// returns the current body (possibly last-known-good); a Complete recording reads the
    /// session index once and is not cached. The gate is released before this returns.
    /// </summary>
    public async Task<string?> GetOrRefreshAsync(RecordingSummary summary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (!summary.IsRecording)
        {
            SessionRead read = await ReadAsync(summary, entry: null, ct).ConfigureAwait(false);
            return read.Kind == SessionReadKind.Complete ? read.Body : null;
        }

        return await RefreshOneAsync(summary, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the session record and folder without the session lock. A Recording session is composed from
    /// its part playlists into <paramref name="entry"/>; without an entry it is not composed.
    /// </summary>
    private async Task<SessionRead> ReadAsync(
        RecordingSummary summary, Entry? entry, CancellationToken ct)
    {
        if (!TryResolveDirectory(summary, out string directory) || !Directory.Exists(directory))
            return SessionRead.Missing;

        // A record-store failure throws: the refresh fails and the last-known-good body stays
        // live instead of being dropped as a deleted session.
        SessionRecord? record = await _store.TryLoadAsync(directory, ct).ConfigureAwait(false);
        if (record is null)
            return SessionRead.Missing;

        if (record.Status == SessionStatus.Complete)
        {
            string? index = await PartPlaylistReader
                .TryReadAllTextAsync(Path.Combine(directory, SessionStore.IndexFileName), ct)
                .ConfigureAwait(false);
            return index is null ? SessionRead.Missing : new SessionRead(SessionReadKind.Complete, index);
        }

        if (entry is null)
            return SessionRead.Missing;

        // The gate serializes builds for this entry, so the composition needs no lock.
        LiveComposition built = await LiveIndexComposer
            .BuildAsync(
                entry.Composition,
                _store.ObservePartFiles(directory),
                _store.MinTargetDuration,
                _source,
                ct)
            .ConfigureAwait(false);
        entry.Composition = built;
        return new SessionRead(SessionReadKind.Live, built.Body);
    }

    /// <summary>The session's folder under the recordings root, refusing ids that would leave it.</summary>
    private bool TryResolveDirectory(RecordingSummary summary, out string directory) =>
        RecordingsPath.TryResolve(
            _storage.Value.RecordingsRoot, [summary.Channel, summary.Id], out directory);

    /// <summary>Removes the entry only if it is still the one this refresh used.</summary>
    private void Remove(string id, Entry entry) =>
        _entries.TryRemove(KeyValuePair.Create(id, entry));

    private void Fail(
        Entry entry,
        RecordingSummary summary,
        DateTimeOffset now,
        Exception exception,
        string outcome)
    {
        (int failures, TimeSpan delay) = entry.Fail(now, _refreshInterval);

        // One Warning per failure window keeps a failing disk visible without flooding the log;
        // later failures in the same window are detail for diagnostics.
        LogLevel level = failures == 1 ? LogLevel.Warning : LogLevel.Debug;
        _logger.Log(
            level,
            exception,
            "Live index refresh {Outcome} for session {SessionId} (failure {FailureCount} in this window); keeping the last-known-good body and retrying in {RetrySeconds:0.#} s.",
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
            // The session record could not be read; the last-known-good body stays live.
            or System.Data.Common.DbException;

    private enum SessionReadKind
    {
        Live,
        Complete,
        Missing,
    }

    private readonly record struct SessionRead(SessionReadKind Kind, string? Body)
    {
        public static SessionRead Missing => new(SessionReadKind.Missing, null);
    }

    /// <summary>
    /// One live session. The gate holder alone builds <see cref="Composition"/> and changes the
    /// backoff; <see cref="Published"/> is swapped atomically so a request never sees a partial body.
    /// </summary>
    private sealed class Entry
    {
        private LiveIndexSnapshot? _published;
        private int _failures;
        private long _nextAttemptUtcTicks;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public LiveComposition? Composition { get; set; }

        public LiveIndexSnapshot? Published
        {
            get => Volatile.Read(ref _published);
            set => Volatile.Write(ref _published, value);
        }

        public long NextAttemptUtcTicks => Volatile.Read(ref _nextAttemptUtcTicks);

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
