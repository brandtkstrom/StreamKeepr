namespace StreamKeepr.Domain;

/// <summary>An input to the pure per-channel reducer.</summary>
public abstract record DvrEvent
{
    private DvrEvent() { }

    public sealed record StreamStatusObserved(StreamObservation Observation, DateTimeOffset At) : DvrEvent;

    public sealed record SessionRecovered(Resumable Resume) : DvrEvent;

    /// <summary>Published once per part, the first time the recorder writes media.</summary>
    public sealed record PartProducingMedia(JobId Job, DateTimeOffset At) : DvrEvent;

    public sealed record RecorderExited(JobId Job, DateTimeOffset At, RecorderExitKind Kind, string Detail) : DvrEvent;

    public sealed record RetryTimerElapsed(JobId Job) : DvrEvent;

    public sealed record StopRequested : DvrEvent;

    public sealed record ResumeRequested : DvrEvent;
}
