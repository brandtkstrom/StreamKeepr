using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using StreamKeepr.Infrastructure.Records;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>Body of <c>PUT /api/recordings/{id}/progress</c>.</summary>
public sealed record ProgressRequest([property: JsonRequired] double PositionSec);

/// <summary>Resume positions for live and finished recordings, shared by every viewer of this app.</summary>
public interface IProgressStore
{
    event Action? Changed;

    double? Get(string recordingId);

    Task SetAsync(string recordingId, double positionSec, CancellationToken ct);

    Task ClearAsync(string recordingId, CancellationToken ct);
}

/// <summary>
/// Resume positions, held in the record store's <c>ResumePositions</c> table (ADR-0014) and read
/// from an in-memory projection loaded at startup. Every change updates the projection, raises
/// <see cref="Changed"/>, then writes the row through; writes run one at a time in the order the
/// changes were made. A failed write is logged and the projection keeps the change, so a broken
/// database never blocks playback; the row catches up on the next successful write for that
/// recording. A finished recording's position within <see cref="EndMargin"/> of the end clears
/// it; a live recording's position is kept wherever it is until the capture finishes, when the
/// same margin applies. Deleting a session record cascades its row.
/// </summary>
public sealed class ProgressStore : IProgressStore, IHostedService, IDisposable
{
    public const double EndMargin = 30;

    private readonly RecordingLibrary _library;
    private readonly IDbContextFactory<RecordStoreDbContext> _records;
    private readonly RecordStoreState _recordStore;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProgressStore> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, double> _positions = new(StringComparer.Ordinal);

    /// <summary>The last queued write; it never faults. Guarded by <see cref="_gate"/>.</summary>
    private Task _writes = Task.CompletedTask;

    public ProgressStore(
        RecordingLibrary library,
        IDbContextFactory<RecordStoreDbContext> records,
        RecordStoreState recordStore,
        TimeProvider clock,
        ILogger<ProgressStore> logger)
    {
        _library = library;
        _records = records;
        _recordStore = recordStore;
        _clock = clock;
        _logger = logger;
        _library.Changed += OnLibraryChanged;
    }

    public event Action? Changed;

    public double? Get(string recordingId)
    {
        ArgumentNullException.ThrowIfNull(recordingId);
        lock (_gate)
            return _positions.TryGetValue(recordingId, out double position) ? position : null;
    }

    public Task SetAsync(string recordingId, double positionSec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recordingId);

        if (!double.IsFinite(positionSec) || _library.Find(recordingId) is not { } summary)
            return Task.CompletedTask;

        // A live recording keeps any position the viewer left behind the live edge. The live
        // edge sits inside the end margin, and the library's duration trails the player's,
        // so neither the margin nor the upper clamp applies until the capture finishes.
        if (summary.IsRecording)
            return Apply(recordingId, Math.Max(0, positionSec));

        if (summary.DurationSeconds is not double duration || !double.IsFinite(duration))
            return Task.CompletedTask;

        double position = Math.Clamp(positionSec, 0, Math.Max(0, duration));
        return position >= duration - EndMargin
            ? ClearAsync(recordingId, ct)
            : Apply(recordingId, position);
    }

    public Task ClearAsync(string recordingId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recordingId);
        return Apply(recordingId, position: null);
    }

    /// <summary>
    /// Loads the stored positions once at startup. A row that is negative or not finite is
    /// skipped. An unreadable store logs and starts empty: resume positions never stop startup.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        _recordStore.EnsurePrepared();

        List<ResumePositionRow> rows;
        try
        {
            await using RecordStoreDbContext db = await _records.CreateDbContextAsync(ct);
            rows = await db.ResumePositions.AsNoTracking().ToListAsync(ct);
        }
        catch (Exception ex) when (RecordStoreFailures.IsStoreFailure(ex))
        {
            _logger.LogWarning(ex, "Could not read the stored resume positions; starting empty.");
            rows = [];
        }

        lock (_gate)
        {
            _positions.Clear();
            foreach (ResumePositionRow row in rows)
            {
                if (double.IsFinite(row.PositionSeconds) && row.PositionSeconds >= 0)
                    _positions[row.SessionId] = row.PositionSeconds;
            }
        }

        // The library may have finished loading first; drop entries it no longer allows.
        OnLibraryChanged();
    }

    /// <summary>Completes once every write queued so far has finished.</summary>
    public Task FlushAsync(CancellationToken ct)
    {
        Task writes;
        lock (_gate)
            writes = _writes;
        return writes.WaitAsync(ct);
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    // Writes are already through; shutdown only waits for one still in flight.
    Task IHostedService.StopAsync(CancellationToken cancellationToken) => FlushAsync(cancellationToken);

    public void Dispose() => _library.Changed -= OnLibraryChanged;

    /// <summary>Sets (<paramref name="position"/>) or clears (null) one entry, then writes it through.</summary>
    private Task Apply(string recordingId, double? position)
    {
        Task write;
        lock (_gate)
        {
            if (!ApplyLocked(recordingId, position))
                return Task.CompletedTask;
            write = EnqueueLocked(recordingId, position);
        }

        Changed?.Invoke();
        return write;
    }

    private bool ApplyLocked(string recordingId, double? position)
    {
        if (position is not double value)
            return _positions.Remove(recordingId);

        if (_positions.TryGetValue(recordingId, out double existing) && existing == value)
            return false;

        _positions[recordingId] = value;
        return true;
    }

    /// <summary>Queues the row write behind every earlier one. Callers hold <see cref="_gate"/>.</summary>
    private Task EnqueueLocked(string recordingId, double? position)
    {
        Task previous = _writes;
        DateTimeOffset at = _clock.GetUtcNow();
        _writes = WriteAfterAsync(previous, recordingId, position, at);
        return _writes;
    }

    private async Task WriteAfterAsync(
        Task previous, string recordingId, double? position, DateTimeOffset at)
    {
        // A write that failed for any other reason already faulted its own caller; it must not
        // stop the writes queued behind it.
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await WriteAsync(recordingId, position, at).ConfigureAwait(false);
    }

    /// <summary>
    /// Upserts or deletes one row. The write is not cancellable: the projection has already
    /// changed, so the row must follow it. Store failures are logged and never thrown (ADR-0014).
    /// </summary>
    private async Task WriteAsync(string recordingId, double? position, DateTimeOffset at)
    {
        try
        {
            await using RecordStoreDbContext db = await _records.CreateDbContextAsync(CancellationToken.None);
            IQueryable<ResumePositionRow> row = db.ResumePositions.Where(stored => stored.SessionId == recordingId);

            if (position is not double value)
            {
                await row.ExecuteDeleteAsync(CancellationToken.None);
                return;
            }

            int updated = await row.ExecuteUpdateAsync(
                set => set
                    .SetProperty(stored => stored.PositionSeconds, value)
                    .SetProperty(stored => stored.UpdatedAt, at),
                CancellationToken.None);
            if (updated > 0)
                return;

            // Only this store writes the table and its writes are serialized, so the insert
            // cannot race another one. A session without a record fails its foreign key.
            db.ResumePositions.Add(new ResumePositionRow
            {
                SessionId = recordingId,
                PositionSeconds = value,
                UpdatedAt = at,
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (RecordStoreFailures.IsStoreFailure(ex))
        {
            // Resume positions are best effort: playback and recording carry on regardless.
            _logger.LogWarning(
                ex, "Could not save the resume position for {RecordingId}; it is kept in memory.", recordingId);
        }
    }

    // Delete removes the recording from the library first, so pruning here clears its entry
    // (the row itself cascades with the session record). A capture that finishes applies the
    // end margin suspended while it was live: a position left within EndMargin of the final end
    // is dropped, as SetAsync would for a finished recording. A finished duration never shrinks,
    // so checking every change is that rule.
    private void OnLibraryChanged()
    {
        if (!_library.IsLoaded)
            return;

        bool changed;
        lock (_gate)
        {
            string[] gone = _positions
                .Where(entry => _library.Find(entry.Key) switch
                {
                    null => true,
                    { IsRecording: false, DurationSeconds: double duration } =>
                        double.IsFinite(duration) && entry.Value >= duration - EndMargin,
                    _ => false,
                })
                .Select(entry => entry.Key)
                .ToArray();
            foreach (string id in gone)
            {
                _positions.Remove(id);
                // Not awaited: library events are synchronous. FlushAsync observes it.
                _ = EnqueueLocked(id, position: null);
            }

            changed = gone.Length > 0;
        }

        if (changed)
            Changed?.Invoke();
    }
}
