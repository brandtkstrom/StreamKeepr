using System.Security.Claims;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Ui;

/// <summary>In-memory <see cref="IExportService"/> whose jobs a test moves by hand.</summary>
internal sealed class TestExportService : IExportService
{
    private readonly Dictionary<string, ExportJob> _jobs = new(StringComparer.Ordinal);

    public List<(string RecordingId, ClaimsPrincipal User)> Starts { get; } = [];

    public Func<string, Exception?> StartFailure { get; set; } = _ => null;

    public List<(string ExportId, ClaimsPrincipal User)> Cancels { get; } = [];

    /// <summary>When set, Cancel throws it instead of cancelling the job.</summary>
    public Func<string, Exception?> CancelFailure { get; set; } = _ => null;

    public event Action? Changed;

    public ExportJob Start(string recordingId, ClaimsPrincipal user, CancellationToken ct)
    {
        if (StartFailure(recordingId) is { } failure)
            throw failure;

        Starts.Add((recordingId, user));
        var job = new ExportJob($"e{_jobs.Count + 1}", recordingId, ExportState.Queued, 0, null, null);
        Set(job);
        return job;
    }

    public ExportJob? Find(string exportId) => _jobs.GetValueOrDefault(exportId);

    public ExportJob? FindByRecording(string recordingId) =>
        _jobs.Values.LastOrDefault(job => job.RecordingId == recordingId);

    public string? FilePath(string exportId) => null;

    public void Cancel(string exportId, ClaimsPrincipal user)
    {
        Cancels.Add((exportId, user));
        if (CancelFailure(exportId) is { } failure)
            throw failure;

        Set(_jobs[exportId] with { State = ExportState.Cancelled });
    }

    /// <summary>Replaces (or adds) a job and raises <see cref="Changed"/>.</summary>
    public void Set(ExportJob job)
    {
        _jobs.Remove(job.Id);
        _jobs[job.Id] = job;
        Changed?.Invoke();
    }
}
