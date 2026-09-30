namespace StreamRecorder.Core;

public sealed record StreamInfo(
    string Channel,
    string Title,
    DateTimeOffset StartTime);