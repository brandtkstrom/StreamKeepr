using System.Collections.Immutable;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>Lifecycle of one session. UI states such as Reconnecting are derived.</summary>
public enum SessionStatus
{
    Recording,
    Complete,
}

/// <summary>
/// One session as the record store holds it (ADR-0014): its <c>Sessions</c> row and its
/// <c>SessionMetadataChanges</c> in time order. <see cref="Metadata"/> starts with the title at
/// stream start; later entries are title/game changes. The final aggregates are null while the
/// session records: live counts and duration are derived from its part playlists instead.
/// </summary>
public sealed record SessionRecord(
    string Id,
    string Channel,
    string StreamId,
    DateTimeOffset StartedAt,
    SessionStatus Status,
    ImmutableList<MetadataChange> Metadata,
    DateTimeOffset? EndedAt = null,
    double? DurationSeconds = null,
    long? SizeBytes = null,
    int? PartCount = null,
    int? SegmentCount = null,
    bool HasThumbnail = false,
    bool HasChatSession = false);
