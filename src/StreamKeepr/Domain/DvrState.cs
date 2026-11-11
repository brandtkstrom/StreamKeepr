namespace StreamKeepr.Domain;

/// <summary>
/// The closed set of per-channel states. Only the reducer creates transitions between them.
/// </summary>
public abstract record DvrState
{
    private DvrState() { }

    /// <summary>
    /// No recorder is running. <see cref="Resume"/> and <see cref="Finished"/> are mutually
    /// exclusive; if both are constructed externally, <see cref="Finished"/> takes precedence.
    /// </summary>
    public sealed record Idle(
        Resumable? Resume = null, int Streak = 0, Resumable? Finished = null) : DvrState;

    /// <summary>
    /// A recorder job is running. <see cref="HasMedia"/> always matches
    /// <see cref="FirstMediaAt"/> being non-null, and <see cref="Attempt"/> counts retries
    /// within the current part.
    /// </summary>
    public sealed record Recording(
        Session Session, JobId Job, LiveStream Latest, int Attempt, bool HasMedia,
        int Streak, DateTimeOffset? FirstMediaAt = null) : DvrState;

    /// <summary>A retry is scheduled; <see cref="Next"/> is the part to start.</summary>
    public sealed record WaitingToRetry(
        Session Session, JobId Next, LiveStream Latest, int Attempt, int Streak,
        int OfflineCount = 0) : DvrState;

    /// <summary>Repeated failures suppress recording until <see cref="Until"/>.</summary>
    public sealed record CoolingDown(
        DateTimeOffset Until, Resumable? Resume, string LastError) : DvrState;

    /// <summary>Recording is stopped and live observations are ignored until resume.</summary>
    public sealed record Paused(Resumable? Finished = null) : DvrState;
}
