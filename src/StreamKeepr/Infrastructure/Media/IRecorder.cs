using StreamKeepr.Domain;

namespace StreamKeepr.Infrastructure.Media;

/// <summary>
/// Runs exactly one recorder job for one part and returns its <see cref="PartOutcome"/> on every
/// path. Implementations publish <see cref="DvrEvent.PartProducingMedia"/> at most once, never
/// publish <see cref="DvrEvent.RecorderExited"/> (the coordinator publishes it from the outcome),
/// and return when <c>stopRequested</c> is canceled. An unverified shutdown is reported as
/// <see cref="PartOutcome.Fault"/>, not thrown. The coordinator depends on this seam so runtime
/// tests can exercise command handling without child processes.
/// </summary>
public interface IRecorder
{
    Task<PartOutcome> RunAsync(
        Session session,
        JobId job,
        Func<DvrEvent, CancellationToken, ValueTask> publish,
        CancellationToken stopRequested = default,
        Action<JobId, long, TimeSpan>? reportProgress = null);
}
