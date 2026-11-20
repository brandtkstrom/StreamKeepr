using System.Collections.Concurrent;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// The request-path authority for live playlists: an in-memory map from session id to the
/// latest immutable snapshot. Publishing swaps a reference with <see cref="Volatile.Write{T}(ref T, T)"/>;
/// readers take one reference with <see cref="Volatile.Read{T}(ref T)"/>, so a request can never
/// observe a half-built body.
/// </summary>
public sealed class LivePlaylistCache
{
    private sealed class Slot
    {
        public LivePlaylistSnapshot? Snapshot;
    }

    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    /// <summary>The current snapshot for a session, or null before the first successful refresh.</summary>
    public LivePlaylistSnapshot? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _slots.TryGetValue(id, out Slot? slot)
            ? Volatile.Read(ref slot.Snapshot)
            : null;
    }

    /// <summary>Atomically replaces the snapshot for its session id.</summary>
    public void Publish(LivePlaylistSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Slot slot = _slots.GetOrAdd(snapshot.Id, static _ => new Slot());
        Volatile.Write(ref slot.Snapshot, snapshot);
    }

    /// <summary>Drops a session after it finalizes or leaves the library. A racing read sees old or null.</summary>
    public void Remove(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        _slots.TryRemove(id, out _);
    }

    /// <summary>A snapshot of the keys, for pruning and diagnostics.</summary>
    public IReadOnlyList<string> Ids => _slots.Keys.ToArray();
}
