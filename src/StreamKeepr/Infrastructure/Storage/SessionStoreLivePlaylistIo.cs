using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Reads the manifest and part playlists for a live refresh without the session manifest lock.
/// Each session keeps an immutable <see cref="LiveSnapshot"/>: a warm refresh returns its body
/// after cheap metadata checks, an append-only change extends it, and any other change rebuilds
/// from the parsed parts already in memory. Only the active last part (or the previous last part
/// once a new part appears) is read from disk again.
/// </summary>
public sealed class SessionStoreLivePlaylistIo : ILivePlaylistIo, ILivePlaylistSnapshotInvalidator
{
    private readonly SessionStore _store;
    private readonly IOptions<StorageOptions> _storage;
    private readonly IPartPlaylistSource _source;
    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    public SessionStoreLivePlaylistIo(SessionStore store, IOptions<StorageOptions> storage)
        : this(store, storage, FilePartPlaylistSource.Instance)
    {
    }

    internal SessionStoreLivePlaylistIo(
        SessionStore store,
        IOptions<StorageOptions> storage,
        IPartPlaylistSource source)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(source);
        _store = store;
        _storage = storage;
        _source = source;
    }

    public async Task<LivePlaylistRead> ReadAsync(
        RecordingSummary summary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (!RecordingsPath.TryResolve(
                _storage.Value.RecordingsRoot, [summary.Channel, summary.Id], out string directory))
        {
            Invalidate(summary.Id);
            return LivePlaylistRead.Missing;
        }

        if (!File.Exists(Path.Combine(directory, SessionStore.ManifestFileName)))
        {
            Invalidate(summary.Id);
            return LivePlaylistRead.Missing;
        }

        SessionManifest? manifest = await _store.TryLoadAsync(directory, ct).ConfigureAwait(false);
        if (manifest is null)
        {
            // The manifest exists but could not be read. Treat it as a refresh failure so the
            // last-known-good body stays live instead of being pruned as a deleted session.
            throw new InvalidDataException(
                $"The session manifest for {summary.Id} is unreadable.");
        }

        if (manifest.Status == SessionStatus.Complete)
        {
            Invalidate(summary.Id);
            string? index = await PartPlaylistReader
                .TryReadAllTextAsync(Path.Combine(directory, SessionStore.IndexFileName), ct)
                .ConfigureAwait(false);
            return index is null ? LivePlaylistRead.Missing : LivePlaylistRead.Complete(index);
        }

        List<(int Part, string Path)> files = _store.PartPlaylistFiles(directory);
        Slot slot = _slots.GetOrAdd(summary.Id, static _ => new Slot());
        long epoch = Volatile.Read(ref slot.Epoch);
        LiveSnapshot? previous = Volatile.Read(ref slot.Snapshot);

        var observations = new PartFileObservation[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            var info = new FileInfo(files[i].Path);
            observations[i] = new PartFileObservation(
                files[i].Part,
                files[i].Path,
                info.Length,
                info.LastWriteTimeUtc.Ticks);
        }

        LiveSnapshot built = await LivePlaylistComposer
            .BuildAsync(previous, observations, _store.MinTargetDuration, _source, ct)
            .ConfigureAwait(false);

        // Finalize, reopen, or delete may have invalidated this session while the build was in
        // flight. The library re-check in LivePlaylistService is the final authority, but a
        // stale build still must not overwrite a snapshot published for the newer lifecycle.
        if (Volatile.Read(ref slot.Epoch) == epoch)
            Volatile.Write(ref slot.Snapshot, built);

        return LivePlaylistRead.Live(built.Body);
    }

    void ILivePlaylistSnapshotInvalidator.Remove(string id) => Invalidate(id);

    private void Invalidate(string id)
    {
        if (_slots.TryGetValue(id, out Slot? slot))
        {
            Interlocked.Increment(ref slot.Epoch);
            Volatile.Write(ref slot.Snapshot, null);
        }
    }

    /// <summary>
    /// One session's snapshot plus an invalidation epoch. The live service's per-session gate
    /// serializes builds; the epoch only guards a build that races finalize, reopen, or delete.
    /// </summary>
    private sealed class Slot
    {
        public LiveSnapshot? Snapshot;
        public long Epoch;
    }
}
