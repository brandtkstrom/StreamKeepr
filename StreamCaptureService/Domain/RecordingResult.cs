namespace StreamRecorder.Domain;

public enum RecordingOutcome
{
    StreamEnded,
    Failed
}

public sealed record RecordingResult(
    RecordingOutcome Outcome,
    string OutputDirectory,
    int? FfmpegExitCode,
    int SegmentCount,
    long BytesWritten); // total size of the folder; 0 when an empty folder was deleted