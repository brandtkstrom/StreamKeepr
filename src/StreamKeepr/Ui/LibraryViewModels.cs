using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Ui;

/// <summary>One streamer group in the library, projected from the recording index.</summary>
public sealed record StreamerView(
    string Login,
    string DisplayName,
    string? AvatarUrl,
    bool IsRecording,
    bool IsPaused,
    bool IsWatched,
    int RecordingCount,
    DateTimeOffset? LastRecordedAt);

/// <summary>The four library states. Reconnecting is a recording without an active job.</summary>
public enum RecordingState
{
    Recording,
    Reconnecting,
    Partial,
    Saved,
}

/// <summary>One library row, projected from a session summary and live DVR status.</summary>
public sealed record RecordingView(
    string Id,
    string StreamerLogin,
    string Title,
    string? Category,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    double DurationSec,
    RecordingState State,
    int SegmentCount,
    int PartCount,
    long? SizeBytes,
    string? ThumbnailUrl,
    string PlaylistUrl,
    double? ResumeSec,
    IReadOnlyList<Chapter> Chapters,
    bool HasChat = false);

/// <summary>Free and total bytes for the recordings volume.</summary>
public sealed record StorageView(long FreeBytes, long TotalBytes);

/// <summary>Library query keys. Missing or invalid values fall back to the documented defaults.</summary>
public sealed record LibraryQuery(string? State = null, string? Search = null, string Sort = "newest");

/// <summary>
/// Scoped public-read projection of the recording library. Components call this instead of
/// reading <see cref="RecordingLibrary"/> or <see cref="Infrastructure.Runtime.DvrStatus"/>.
/// </summary>
public interface ILibraryView : IDisposable
{
    event Action? Changed;

    /// <summary>True after startup recovery has loaded every session.</summary>
    bool IsLoaded { get; }

    IReadOnlyList<StreamerView> Streamers(LibraryQuery query);

    IReadOnlyList<RecordingView> Recordings(string streamerLogin, LibraryQuery query);

    RecordingView? Find(string id);

    StorageView Storage { get; }
}
