using System.Collections.Immutable;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>Lifecycle of one session folder. UI states such as Reconnecting are derived.</summary>
public enum SessionStatus
{
    Recording,
    Complete,
}

/// <summary>
/// The on-disk <c>session.json</c> schema, version 1. <c>Metadata</c> starts with the title at
/// stream start; later entries are title/game changes.
/// </summary>
public sealed record SessionManifest(
    int Version,
    string Id,
    string Channel,
    string StreamId,
    DateTimeOffset StartedAt,
    SessionStatus Status,
    ImmutableList<MetadataChange> Metadata,
    DateTimeOffset? EndedAt = null,
    double? DurationSeconds = null,
    long? SizeBytes = null);
