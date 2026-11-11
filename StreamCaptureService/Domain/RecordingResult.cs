namespace StreamRecorder.Domain;

public enum RecordingOutcome
{
    Cancelled, StreamEnded, Failed
}

public sealed record RecordingResult(
    RecordingOutcome Outcome,
    string OutputDirectory);
