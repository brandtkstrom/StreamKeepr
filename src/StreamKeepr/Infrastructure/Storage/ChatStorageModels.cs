using System.Text.Json.Serialization;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>One persisted chat message. Positions are Unicode code points.</summary>
internal sealed record StoredChatMessage(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("at")] long At,
    [property: JsonPropertyName("local")] bool? Local,
    [property: JsonPropertyName("uid")] string? UserId,
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("display")] string Display,
    [property: JsonPropertyName("color")] string? Color,
    [property: JsonPropertyName("badges")] IReadOnlyList<StoredChatBadge> Badges,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("emotes")] IReadOnlyList<StoredChatEmote> Emotes,
    [property: JsonPropertyName("bits")] int? Bits,
    [property: JsonPropertyName("reply")] StoredChatReply? Reply,
    [property: JsonPropertyName("action")] bool Action);

public sealed record StoredChatBadge(
    [property: JsonPropertyName("set")] string Set,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("info")] string? Info);

public sealed record StoredChatEmote(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ranges")] IReadOnlyList<StoredChatRange> Ranges);

public sealed record StoredChatRange(
    [property: JsonPropertyName("from")] int Start,
    [property: JsonPropertyName("to")] int End);

public sealed record StoredChatReply(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("login")] string? Login,
    [property: JsonPropertyName("display")] string? Display,
    [property: JsonPropertyName("text")] string? Text);

/// <summary>Advisory per-session chat metadata; the session manifest remains authoritative.</summary>
internal sealed record StoredChatMeta(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("transport")] string Transport,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("endedAt")] DateTimeOffset? EndedAt,
    [property: JsonPropertyName("leadInSec")] int LeadInSec,
    [property: JsonPropertyName("bucketSeconds")] int BucketSeconds,
    [property: JsonPropertyName("lastMessageAt")] DateTimeOffset? LastMessageAt,
    [property: JsonPropertyName("messageCount")] int MessageCount,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("purgedCount")] int? PurgedCount,
    [property: JsonPropertyName("purgedAt")] DateTimeOffset? PurgedAt,
    [property: JsonPropertyName("purgedDeletedText")] bool? PurgedDeletedText);

/// <summary>The <c>kind</c> values written to <c>events.jsonl</c>.</summary>
internal static class ChatEventKinds
{
    public const string Delete = "delete";
    public const string ClearAll = "clear_all";
    public const string ClearUser = "clear_user";
    public const string RoomState = "roomstate";
    public const string Notice = "notice";
    public const string Reconnect = "reconnect";
    public const string Gap = "gap";
    public const string Dropped = "dropped";
    public const string Truncated = "truncated";
    public const string End = "end";
    public const string Purged = "purged";

    /// <summary>Kinds that carry removed text; the purge drops them once applied.</summary>
    public static bool IsTombstone(string kind) => kind is Delete or ClearUser or ClearAll;
}

/// <summary>One persisted non-message chat event.</summary>
internal sealed record StoredChatEvent(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("at")] long At,
    [property: JsonPropertyName("targetId")] string? TargetId,
    [property: JsonPropertyName("targetUid")] string? TargetUid,
    [property: JsonPropertyName("targetLogin")] string? TargetLogin,
    [property: JsonPropertyName("from")] long? From,
    [property: JsonPropertyName("to")] long? To,
    [property: JsonPropertyName("count")] int? Count,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("emoteOnly")] bool? EmoteOnly,
    [property: JsonPropertyName("subsOnly")] bool? SubscribersOnly,
    [property: JsonPropertyName("uniqueChat")] bool? UniqueChat,
    [property: JsonPropertyName("slow")] int? Slow);

public sealed record ChatQueryMessage(
    string Id,
    double Mt,
    long At,
    string? Uid,
    string Login,
    string Display,
    string? Color,
    IReadOnlyList<StoredChatBadge> Badges,
    string Text,
    IReadOnlyList<StoredChatEmote> Emotes,
    int? Bits,
    StoredChatReply? Reply,
    bool Action);

public sealed record ChatQueryGap(double From, double To);

public sealed record ChatQueryResult(
    string RecordingId,
    string State,
    bool HasChat,
    string Source,
    int LeadInSec,
    double FromSec,
    double ToSec,
    double SyncOffsetSec,
    IReadOnlyList<ChatQueryMessage> Messages,
    IReadOnlyList<ChatQueryGap> Gaps,
    bool HasMoreBefore,
    bool HasMoreAfter,
    [property: JsonPropertyName("v")] int Version = 1);
