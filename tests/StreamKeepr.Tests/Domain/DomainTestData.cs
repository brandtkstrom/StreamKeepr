using StreamKeepr.Domain;

namespace StreamKeepr.Tests.Domain;

internal static class DomainTestData
{
    public const string RecordingsRoot = "/recordings";
    public const string Channel = "streamer";

    public static readonly DateTimeOffset StartedAt =
        new(2026, 11, 10, 18, 30, 0, TimeSpan.FromHours(2));

    public static DvrSettings Settings { get; } = new(
        RecordingsRoot,
        Channel,
        MaxRetries: 6,
        RetryBase: TimeSpan.FromSeconds(2),
        RetryMax: TimeSpan.FromMinutes(1),
        FailuresBeforeCooldown: 3,
        Cooldown: TimeSpan.FromMinutes(15),
        HealthyPart: TimeSpan.FromMinutes(1));

    public static LiveStream Live(
        string streamId = "123456789",
        string title = "A live stream",
        string game = "Just Chatting",
        DateTimeOffset? startedAt = null,
        string? thumbnailUrl = null) =>
        new(streamId, title, game, startedAt ?? StartedAt, thumbnailUrl);

    public static DvrState.Recording Recording(
        LiveStream? live = null,
        int attempt = 0,
        bool hasMedia = false,
        int streak = 0,
        DateTimeOffset? firstMediaAt = null,
        int part = 1)
    {
        live ??= Live();
        var session = Session.For(live, Channel, RecordingsRoot);
        return new DvrState.Recording(
            session,
            new JobId(session.Id, part),
            live,
            attempt,
            hasMedia,
            streak,
            firstMediaAt);
    }

    public static DvrEvent ObservedLive(LiveStream live, DateTimeOffset? at = null) =>
        new DvrEvent.StreamStatusObserved(
            new StreamObservation.Live(live), at ?? live.StartedAt);

    public static DvrEvent Observed(StreamObservation observation, DateTimeOffset? at = null) =>
        new DvrEvent.StreamStatusObserved(observation, at ?? StartedAt);
}
