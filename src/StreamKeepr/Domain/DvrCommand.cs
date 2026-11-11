namespace StreamKeepr.Domain;

/// <summary>
/// An intent emitted by the reducer. The coordinator executes commands sequentially; the
/// reducer never performs I/O.
/// </summary>
public abstract record DvrCommand
{
    private DvrCommand() { }

    public sealed record OpenSession(Session Session, LiveStream Stream) : DvrCommand;

    public sealed record ReopenSession(Session Session) : DvrCommand;

    public sealed record RecordMetadata(Session Session, LiveStream Stream) : DvrCommand;

    public sealed record StartRecorder(Session Session, JobId Job) : DvrCommand;

    public sealed record StopRecorder(JobId Job) : DvrCommand;

    public sealed record ScheduleRetry(JobId Job, TimeSpan BaseDelay) : DvrCommand;

    public sealed record FinalizeSession(Session Session) : DvrCommand;
}
