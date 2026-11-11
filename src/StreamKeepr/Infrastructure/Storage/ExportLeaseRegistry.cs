namespace StreamKeepr.Infrastructure.Storage;

/// <summary>Leases that keep a recording from being deleted while an export reads it.</summary>
public interface IExportLeaseRegistry
{
    IDisposable Acquire(string recordingId);

    bool IsLeased(string recordingId);
}

public sealed class ExportLeaseRegistry : IExportLeaseRegistry
{
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public IDisposable Acquire(string recordingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingId);
        lock (_gate)
            _leases[recordingId] = _leases.GetValueOrDefault(recordingId) + 1;
        return new Lease(this, recordingId);
    }

    public bool IsLeased(string recordingId)
    {
        lock (_gate)
            return _leases.ContainsKey(recordingId);
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

    private sealed class Lease(ExportLeaseRegistry owner, string recordingId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release(recordingId);
        }
    }
}
