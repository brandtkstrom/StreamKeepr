using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Ui;

/// <summary>In-memory <see cref="IProgressStore"/> that records every call.</summary>
internal sealed class TestProgressStore : IProgressStore
{
    public Dictionary<string, double> Positions { get; } = new(StringComparer.Ordinal);

    public List<string> Cleared { get; } = [];

    public event Action? Changed;

    public double? Get(string recordingId) =>
        Positions.TryGetValue(recordingId, out double position) ? position : null;

    public Task SetAsync(string recordingId, double positionSec, CancellationToken ct)
    {
        Positions[recordingId] = positionSec;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task ClearAsync(string recordingId, CancellationToken ct)
    {
        Positions.Remove(recordingId);
        Cleared.Add(recordingId);
        Changed?.Invoke();
        return Task.CompletedTask;
    }
}
