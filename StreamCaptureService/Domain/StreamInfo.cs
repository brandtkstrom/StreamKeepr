namespace StreamRecorder.Domain;

public sealed record StreamInfo(
    string StreamId,
    string UserLogin,
    string UserName,
    string Title,
    DateTimeOffset StartedAt);
