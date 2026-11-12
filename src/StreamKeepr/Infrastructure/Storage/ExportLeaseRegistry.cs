namespace StreamKeepr.Infrastructure.Storage;

/// <summary>Leases that keep a recording from being deleted while an export reads it.</summary>
public interface IExportLeaseRegistry
{
    /// <summary>Acquires a lease; throws <see cref="InvalidOperationException"/> while a delete holds the recording.</summary>
    IDisposable Acquire(string recordingId);

    bool IsLeased(string recordingId);

    /// <summary>
    /// Starts a delete. Returns null while any export lease is held; a returned guard blocks new
    /// leases until it is disposed.
    /// </summary>
    IDisposable? TryBeginDelete(string recordingId);
}

public sealed class ExportLeaseRegistry : IExportLeaseRegistry
{
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deleting = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public IDisposable Acquire(string recordingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingId);
        lock (_gate)
        {
            if (_deleting.Contains(recordingId))
                throw new InvalidOperationException("That recording is being deleted.");

            _leases[recordingId] = _leases.GetValueOrDefault(recordingId) + 1;
            return new Lease(this, recordingId);
        }
    }

    public bool IsLeased(string recordingId)
    {
        lock (_gate)
            return _leases.ContainsKey(recordingId);
    }

    public IDisposable? TryBeginDelete(string recordingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingId);
        lock (_gate)
        {
            if (_leases.ContainsKey(recordingId) || !_deleting.Add(recordingId))
                return null;

            return new DeleteGuard(this, recordingId);
        }
    }

    private void Release(string recordingId)
    {
        lock (_gate)
        {
            if (!_leases.TryGetValue(recordingId, out int count))
                return;
            if (count <= 1)
                _leases.Remove(recordingId);
            else
                _leases[recordingId] = count - 1;
        }
    }

    private void EndDelete(string recordingId)
    {
        lock (_gate)
            _deleting.Remove(recordingId);
    }

    private sealed class Lease(ExportLeaseRegistry owner, string recordingId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release(recordingId);
        }
    }

    private sealed class DeleteGuard(ExportLeaseRegistry owner, string recordingId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.EndDelete(recordingId);
        }
    }
}
