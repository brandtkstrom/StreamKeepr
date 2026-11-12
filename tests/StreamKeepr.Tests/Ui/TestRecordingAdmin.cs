using System.Security.Claims;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Ui;

internal sealed class TestRecordingAdmin : IRecordingAdmin
{
    public List<(string RecordingId, ClaimsPrincipal User)> Deletes { get; } = [];

    /// <summary>When set, DeleteAsync waits for it before finishing.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>When set, DeleteAsync throws it instead of succeeding.</summary>
    public Func<string, Exception?> Failure { get; set; } = _ => null;

    public async Task DeleteAsync(string recordingId, ClaimsPrincipal user, CancellationToken ct)
    {
        Deletes.Add((recordingId, user));
        if (Gate is not null)
            await Gate.Task;

        if (Failure(recordingId) is { } failure)
            throw failure;
    }
}
