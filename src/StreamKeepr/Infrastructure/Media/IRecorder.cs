using StreamKeepr.Domain;

namespace StreamKeepr.Infrastructure.Media;

/// <summary>
/// Runs exactly one recorder job for one part. Implementations publish exactly one
/// <see cref="DvrEvent.RecorderExited"/>, publish <see cref="DvrEvent.PartProducingMedia"/> at most once,
/// and return when <c>stopRequested</c> is canceled. The coordinator depends on this seam so runtime
/// tests can exercise command handling without child processes.
/// </summary>
public interface IRecorder
{
    Task RunAsync(
        Session session,
        JobId job,
        Func<DvrEvent, CancellationToken, ValueTask> publish,
        CancellationToken stopRequested = default,
        Action<JobId, long, TimeSpan>? reportProgress = null);
}
