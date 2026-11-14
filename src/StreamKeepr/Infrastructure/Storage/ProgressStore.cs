using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;

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
/// Keeps positions in memory and persists them to <c>.streamkeepr/progress.json</c>. Writes
/// are atomic, at most one every <see cref="WriteInterval"/>, plus a flush on shutdown. A finished
/// recording's position within <see cref="EndMargin"/> of the end clears it; a live recording's
/// position is kept wherever it is until the capture finishes, when the same margin applies.
/// </summary>
public sealed class ProgressStore : IProgressStore, IHostedService, IDisposable
{
    public static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(10);
    public const double EndMargin = 30;

    private readonly RecordingLibrary _library;
    private readonly IOptions<StorageOptions> _storage;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProgressStore> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<string, double> _positions = new(StringComparer.Ordinal);

    private ITimer? _timer;
    private DateTimeOffset? _lastWrite;
    private bool _dirty;
    private bool _disposed;

    public ProgressStore(
        RecordingLibrary library,
        IOptions<StorageOptions> storage,
        TimeProvider clock,
        ILogger<ProgressStore> logger)
    {
        _library = library;
        _storage = storage;
        _clock = clock;
        _logger = logger;
        _library.Changed += OnLibraryChanged;
    }

    public event Action? Changed;

    private string FilePath => AppDataPaths.ProgressFile(_storage.Value.RecordingsRoot);

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
            return Store(recordingId, Math.Max(0, positionSec));

        if (summary.DurationSeconds is not double duration || !double.IsFinite(duration))
            return Task.CompletedTask;

        double position = Math.Clamp(positionSec, 0, Math.Max(0, duration));
        return position >= duration - EndMargin
            ? ClearAsync(recordingId, ct)
            : Store(recordingId, position);
    }

    private Task Store(string recordingId, double position) =>
        Apply(positions =>
        {
            if (positions.TryGetValue(recordingId, out double existing) && existing == position)
                return false;
            positions[recordingId] = position;
            return true;
        });

    public Task ClearAsync(string recordingId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recordingId);
        return Apply(positions => positions.Remove(recordingId));
    }

    /// <summary>Reads the file once at startup. A missing or unreadable file starts empty.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        Dictionary<string, double>? loaded = null;
        try
        {
            if (File.Exists(FilePath))
            {
                await using FileStream stream = File.OpenRead(FilePath);
                loaded = await JsonSerializer.DeserializeAsync<Dictionary<string, double>>(
                    stream, cancellationToken: ct);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read watch progress at {Path}; starting empty.", FilePath);
        }

        lock (_gate)
        {
            _positions.Clear();
            foreach ((string id, double position) in loaded ?? [])
            {
                if (double.IsFinite(position) && position >= 0)
                    _positions[id] = position;
            }
        }

        // The library may have finished loading first; drop entries for deleted recordings.
        OnLibraryChanged();
    }

    /// <summary>Writes any pending change now.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        Dictionary<string, double> snapshot;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            // Shutdown flushes through StopAsync before the container disposes the store.
            if (!_dirty || _disposed)
                return;
            _dirty = false;
            _lastWrite = _clock.GetUtcNow();
            snapshot = new Dictionary<string, double>(_positions, StringComparer.Ordinal);
        }

        await _writeGate.WaitAsync(ct);
        try
        {
            string directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            string temp = FilePath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(snapshot), ct);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write watch progress to {Path}.", FilePath);
            lock (_gate)
            {
                _dirty = true;
                ScheduleLocked();
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => FlushAsync(cancellationToken);

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

        _library.Changed -= OnLibraryChanged;

        // Wait out a write already in progress before releasing its gate.
        _writeGate.Wait();
        _writeGate.Dispose();
    }

    private Task Apply(Func<Dictionary<string, double>, bool> change)
    {
        bool changed;
        lock (_gate)
        {
            changed = change(_positions);
            if (changed)
            {
                _dirty = true;
                ScheduleLocked();
            }
        }

        if (changed)
            Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>Arms the write timer for the next allowed slot. Callers hold <see cref="_gate"/>.</summary>
    private void ScheduleLocked()
    {
        if (_disposed || _timer is not null)
            return;

        TimeSpan due = _lastWrite is { } last
            ? last + WriteInterval - _clock.GetUtcNow()
            : WriteInterval;
        if (due < TimeSpan.Zero)
            due = TimeSpan.Zero;

        _timer = _clock.CreateTimer(
            _ => _ = FlushAsync(CancellationToken.None),
            state: null,
            due,
            Timeout.InfiniteTimeSpan);
    }

    // Delete removes the recording from the library first, so pruning here clears its entry.
    // A capture that finishes applies the end margin suspended while it was live: a position
    // left within EndMargin of the final end is dropped, as SetAsync would for a finished
    // recording. A finished duration never shrinks, so checking every change is that rule.
    private void OnLibraryChanged()
    {
        if (!_library.IsLoaded)
            return;

        _ = Apply(positions =>
        {
            string[] gone = positions
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
                positions.Remove(id);
            return gone.Length > 0;
        });
    }
}
