namespace StreamRecorder;

public enum StreamStatus
{
    Unknown, Offline, Live
}

public sealed record StreamInfo
{
    public string Id { get; init; } = string.Empty;

    public string UserId { get; init; } = string.Empty;

    public string UserLogin { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string GameId { get; init; } = string.Empty;

    public string GameName { get; init; } = string.Empty;

    public StreamStatus Type { get; init; } = StreamStatus.Unknown;

    public string Title { get; init; } = string.Empty;

    public List<string> Tags { get; init; } = new();

    public int ViewerCount { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public string Language { get; init; } = string.Empty;

    public string ThumbnailUrl { get; init; } = string.Empty;

    public List<string> TagIds { get; init; } = new();

    public bool IsMature { get; init; }
}