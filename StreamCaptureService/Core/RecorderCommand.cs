namespace StreamRecorder.Core;

/// <summary>Side effects the orchestrator executes for a transition.</summary>
public abstract record RecorderCommand
{
    /// <summary>Starts the pipeline for the given session directory.</summary>
    public sealed record StartPipeline(string SessionPath) : RecorderCommand;

    /// <summary>Cancels the running pipeline's token.</summary>
    public sealed record StopPipeline : RecorderCommand;
}