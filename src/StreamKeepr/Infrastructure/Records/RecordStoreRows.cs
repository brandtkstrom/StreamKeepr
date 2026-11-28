using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Infrastructure.Records;

// Rows of the record store (ADR-0014). The full schema lands in one initial migration so the
// session, chat, playback and sign-in tickets (#316-#319) can fill their tables in parallel;
// change any of these only in coordination with them. Media stays on the filesystem.

/// <summary>A watched channel. <see cref="Position"/> keeps the list in the order it was built.</summary>
public sealed class ChannelRow
{
    public required string Login { get; set; }

    public int Position { get; set; }

    public bool Paused { get; set; }

    public string? UserId { get; set; }

    public string? DisplayName { get; set; }

    public string? AvatarUrl { get; set; }
}

/// <summary>
/// First-start data that has been written, by name. A seed is applied once, in the same
/// transaction as its rows, so a first start that fails before seeding still seeds next time and
/// data the user later removes is never seeded again.
/// </summary>
public sealed class AppliedSeedRow
{
    public required string Name { get; set; }
}

/// <summary>One recording session; final aggregates are null until it is finalized.</summary>
public sealed class SessionRow
{
    public required string Id { get; set; }

    public required string Channel { get; set; }

    public required string StreamId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public SessionStatus Status { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public double? DurationSeconds { get; set; }

    public long? SizeBytes { get; set; }

    public int? PartCount { get; set; }

    public int? SegmentCount { get; set; }

    public bool HasThumbnail { get; set; }
}

/// <summary>A title or game change observed while a session recorded.</summary>
public sealed class SessionMetadataChangeRow
{
    public long Id { get; set; }

    public required string SessionId { get; set; }

    public DateTimeOffset At { get; set; }

    public required string Title { get; set; }

    public required string Game { get; set; }
}

/// <summary>Chat capture state for one session; its presence means the session has chat.</summary>
public sealed class ChatSessionRow
{
    public required string SessionId { get; set; }

    public required string Source { get; set; }

    public required string Transport { get; set; }

    public required string Status { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public int LeadInSeconds { get; set; }

    public DateTimeOffset? LastMessageAt { get; set; }

    public int MessageCount { get; set; }

    /// <summary>Estimated stored bytes, checked against <c>Chat:MaxBytesPerSession</c>.</summary>
    public long StoredBytes { get; set; }

    public bool Truncated { get; set; }

    public string? LastError { get; set; }

    public int? PurgedCount { get; set; }

    public DateTimeOffset? PurgedAt { get; set; }

    public bool? PurgedDeletedText { get; set; }
}

/// <summary>One captured chat message. <see cref="At"/> is Unix milliseconds, as in the chat API.</summary>
public sealed class ChatMessageRow
{
    public long Id { get; set; }

    public required string SessionId { get; set; }

    public required string MessageId { get; set; }

    public long At { get; set; }

    public bool? Local { get; set; }

    public string? UserId { get; set; }

    public required string Login { get; set; }

    public required string Display { get; set; }

    public string? Color { get; set; }

    /// <summary>JSON array of badges, in the chat API's shape.</summary>
    public required string BadgesJson { get; set; }

    /// <summary>Null once a finalize purge removed a deleted message's text.</summary>
    public string? Text { get; set; }

    /// <summary>JSON array of emotes, in the chat API's shape.</summary>
    public required string EmotesJson { get; set; }

    public int? Bits { get; set; }

    public string? ReplyMessageId { get; set; }

    public string? ReplyLogin { get; set; }

    public string? ReplyDisplay { get; set; }

    public string? ReplyText { get; set; }

    public bool IsAction { get; set; }
}

/// <summary>A chat event (deletion, clear, room state, notice, gap, truncation, purge).</summary>
public sealed class ChatEventRow
{
    public long Id { get; set; }

    public required string SessionId { get; set; }

    public required string Kind { get; set; }

    public long At { get; set; }

    public string? TargetMessageId { get; set; }

    public string? TargetUserId { get; set; }

    public string? TargetLogin { get; set; }

    public long? FromAt { get; set; }

    public long? ToAt { get; set; }

    public int? Count { get; set; }

    public string? Reason { get; set; }

    public bool? EmoteOnly { get; set; }

    public bool? SubscribersOnly { get; set; }

    public bool? UniqueChat { get; set; }

    public int? SlowSeconds { get; set; }
}

/// <summary>A viewer count observed while a session recorded, as seconds after its start.</summary>
public sealed class ViewerSampleRow
{
    public long Id { get; set; }

    public required string SessionId { get; set; }

    public double OffsetSeconds { get; set; }

    public int Count { get; set; }
}

/// <summary>The shared resume position for one session.</summary>
public sealed class ResumePositionRow
{
    public required string SessionId { get; set; }

    public double PositionSeconds { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The single admin token, as Data Protection ciphertext. Never plaintext.</summary>
public sealed class AdminTokenRow
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public required string Ciphertext { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The single EventSub conduit this app owns.</summary>
public sealed class EventSubConduitRow
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public required string ConduitId { get; set; }

    public required string ClientId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
