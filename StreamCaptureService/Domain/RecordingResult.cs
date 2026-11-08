namespace StreamRecorder.Domain;

public enum RecordingOutcome
{
     None, StreamEnded, Failed
}

public sealed record RecordingResult(
    RecordingOutcome Outcome,
    string OutputDirectory);