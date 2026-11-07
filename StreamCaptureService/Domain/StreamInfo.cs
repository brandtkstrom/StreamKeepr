namespace StreamRecorder.Domain;

public sealed record StreamInfo
{
    public string StreamId { get; init; } = string.Empty;

    public string UserLogin { get; init; } = string.Empty;

    public string UserName { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
}