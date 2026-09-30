namespace StreamRecorder.Core;

/// <summary>
/// Where the recorder is. States with an active session carry its <c>SessionPath</c>, so a
/// restart after a brief interruption resumes the same playlist and segments. <c>Offline</c>
/// carries nothing: reaching it ends the session and resets the failure budget.
/// </summary>
public abstract record RecorderState
{
    /// <summary>No active session and no pipeline; the failure budget is clear.</summary>
    public sealed record Offline : RecorderState;

    /// <summary>A session was created and the pipeline is being brought up.</summary>
    public sealed record Starting(string SessionPath, int ConsecutiveFailures) : RecorderState;

    /// <summary>The pipeline is running for the session.</summary>
    public sealed record Recording(string SessionPath, int ConsecutiveFailures) : RecorderState;

    /// <summary>The session is ending: a stop was requested or the stream went offline.</summary>
    public sealed record Stopping(int ConsecutiveFailures) : RecorderState;

    /// <summary>
    /// The pipeline exited cleanly while the stream stayed live. The session stays resumable,
    /// so an interruption that does not produce an offline edge continues the same playlist.
    /// </summary>
    public sealed record Interrupted(string SessionPath, int ConsecutiveFailures) : RecorderState;

    /// <summary>
    /// The pipeline failed; retries are bounded by the failure budget. <c>SessionPath</c> is
    /// null only for defensive faults that cannot be tied to a session.
    /// </summary>
    public sealed record Faulted(string? SessionPath, string Reason, int ConsecutiveFailures) : RecorderState;
}