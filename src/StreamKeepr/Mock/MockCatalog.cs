using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Mock;

/// <summary>One fake streamer exposed by the Mock profile.</summary>
public sealed record MockStreamer(string Login, string DisplayName, string UserId);

/// <summary>A title/game change seeded into a mock session; offsets are from stream start.</summary>
public sealed record MockChapter(TimeSpan Offset, string Title, string Game);

/// <summary>
/// One deterministic mock recording. <see cref="Parts"/> is the number of seeded parts;
/// <see cref="Complete"/> marks a finished session. One two-part partial and the growing
/// capture have <see cref="Complete"/> false.
/// </summary>
public sealed record MockRecording(
    string Channel,
    string StreamId,
    string Title,
    string Game,
    DateTimeOffset StartedAt,
    int Parts,
    bool Complete,
    IReadOnlyList<MockChapter> Chapters)
{
    public LiveStream Live => new(StreamId, Title, Game, StartedAt, ThumbnailUrl: null);
}

/// <summary>
/// The one source of truth for Mock profile data: four channels and eight recordings with
/// fixed stream IDs and start times. The poller and seeder both read this catalog so the
/// growing capture always resumes the same session.
/// </summary>
public static class MockCatalog
{
    /// <summary>Fixed seed time so every run has the same IDs and dates.</summary>
    public static readonly DateTimeOffset T0 =
        new(2026, 11, 8, 18, 0, 0, TimeSpan.Zero);

    public static readonly string AdminLogin = "mock_admin";

    public static readonly string AdminDisplayName = "Mock Admin";

    public static readonly IReadOnlyList<MockStreamer> Streamers =
    [
        new("mock_nova", "Nova Vale", "9100001"),
        new("mock_echo", "Echo Lane", "9100003"),
        new("mock_pixel", "Pixel Park", "9100005"),
        new("mock_rain", "Rain Ash", "9100007"),
    ];

    public static readonly IReadOnlyList<MockRecording> Recordings =
    [
        new(
            "mock_nova", "9100001", "Ranked climb to Diamond", "Apex Legends",
            T0, Parts: 1, Complete: true,
            [
                new(TimeSpan.FromSeconds(3), "Ranked climb to Diamond — match 2", "Apex Legends"),
            ]),
        new(
            "mock_nova", "9100002", "Co-op horror night", "Phasmophobia",
            T0.AddDays(1).AddHours(1), Parts: 2, Complete: true,
            [
                new(TimeSpan.FromSeconds(3), "Co-op horror night — the basement", "Phasmophobia"),
                new(TimeSpan.FromSeconds(9), "Co-op horror night — the attic", "Phasmophobia"),
            ]),
        new(
            "mock_echo", "9100003", "Speedrun practice", "Celeste",
            T0.AddHours(2), Parts: 1, Complete: true,
            [
                new(TimeSpan.FromSeconds(2), "Speedrun practice — any%", "Celeste"),
            ]),
        new(
            "mock_echo", "9100004", "Chill art stream", "Art",
            T0.AddDays(1).AddHours(3), Parts: 1, Complete: true,
            []),
        new(
            "mock_pixel", "9100005", "Retro marathon", "Chrono Trigger",
            T0.AddHours(4), Parts: 1, Complete: true,
            [
                new(TimeSpan.FromSeconds(4), "Retro marathon — the trial", "Chrono Trigger"),
            ]),
        new(
            "mock_pixel", "9100006", "Chat picks the route", "Elden Ring",
            T0.AddDays(1).AddHours(5), Parts: 2, Complete: false,
            [
                new(TimeSpan.FromSeconds(5), "Chat picks the route — Liurnia", "Elden Ring"),
            ]),
        new(
            "mock_rain", "9100007", "Late night jazz", "Music",
            T0.AddHours(6), Parts: 1, Complete: true,
            []),
        new(
            "mock_rain", "9100008", "Community day", "Just Chatting",
            T0.AddDays(2).AddHours(5), Parts: 1, Complete: false,
            [
                new(TimeSpan.FromSeconds(3), "Community day — viewer games", "Just Chatting"),
            ]),
    ];

    /// <summary>The seeded session that continues to grow while the Mock profile runs.</summary>
    public static MockRecording Growing =>
        Recordings.Single(recording => recording.Channel == "mock_rain"
            && recording.StreamId == "9100008");

    /// <summary>The recording selected when the library first opens.</summary>
    public static MockRecording Selected => Recordings[0];

    /// <summary>Builds the deterministic session (and its safe folder) for a mock recording.</summary>
    public static Session SessionFor(MockRecording recording, string recordingsRoot)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingsRoot);

        return Session.For(recording.Live, recording.Channel, recordingsRoot);
    }

    /// <summary>Metadata history: the first entry is the stream title, later entries are chapters.</summary>
    public static IReadOnlyList<MetadataChange> MetadataFor(MockRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);

        var metadata = new List<MetadataChange>
        {
            new(recording.StartedAt, recording.Title, recording.Game),
        };

        foreach (MockChapter chapter in recording.Chapters)
            metadata.Add(new(recording.StartedAt + chapter.Offset, chapter.Title, chapter.Game));

        return metadata;
    }
}
