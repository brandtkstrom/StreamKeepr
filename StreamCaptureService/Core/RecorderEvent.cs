namespace StreamRecorder.Core;

public abstract record RecorderEvent
{
    /// <summary>A poll saw the channel transition to live.</summary>
    public sealed record StreamWentLive : RecorderEvent;

    /// <summary>A poll saw the channel transition to offline.</summary>
    public sealed record StreamWentOffline : RecorderEvent;

    /// <summary>The pipeline reported that it is up.</summary>
    public sealed record ProcessStarted : RecorderEvent;

    /// <summary>
    /// The pipeline finished without a failure. Carries no exit code: <c>StreamlinkService</c>
    /// maps a non-zero exit to <see cref="PipelineFailed"/>, while a requested stop — even one
    /// that had to be force-killed — finishes cleanly, so the transition never had a code to read.
    /// </summary>
    public sealed record ProcessExited : RecorderEvent;

    /// <summary>The pipeline failed; the reason is stored on the faulted state.</summary>
    public sealed record PipelineFailed(string Reason) : RecorderEvent;

    /// <summary>A graceful stop was requested (host shutdown).</summary>
    public sealed record StopRequested : RecorderEvent;
}