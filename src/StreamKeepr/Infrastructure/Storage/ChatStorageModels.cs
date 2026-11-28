using System.Text.Json.Serialization;

namespace StreamKeepr.Infrastructure.Storage;

// The chat API's badge, emote and reply shapes. Badges and emotes are stored in this same JSON
// shape in ChatMessages.BadgesJson and EmotesJson. Emote positions are Unicode code points.

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

/// <summary>
/// The <c>state</c> of a chat query and the <c>Status</c> of a <c>ChatSessions</c> row; the pane's
/// ChatPane.razor.js switches on the same strings.
/// </summary>
public static class ChatStates
{
    public const string Starting = "starting";
    public const string Recording = "recording";
    public const string Complete = "complete";
    public const string Unavailable = "unavailable";

    /// <summary>Pane-only: a live recording whose stream is reconnecting.</summary>
    public const string Reconnecting = "reconnecting";
}

/// <summary>
/// The <c>Source</c> of a <c>ChatSessions</c> row and of a chat query. Each <c>IChatSource</c> reports one
/// of these; storage records it as given.
/// </summary>
public static class ChatSources
{
    public const string Anonymous = "irc-anonymous";
    public const string Token = "irc-token";
    public const string Mock = "mock";

    /// <summary>Reported when a recording has no chat log.</summary>
    public const string None = "none";
}

/// <summary>The <c>reason</c> values written on <c>gap</c> and <c>dropped</c> events.</summary>
public static class ChatGapReasons
{
    public const string IrcDisconnected = "irc-disconnected";
    public const string QueueFull = "queue-full";
    public const string StoreFailed = "store-failed";
}

/// <summary>The <c>Kind</c> values of <c>ChatEvents</c> rows.</summary>
internal static class ChatEventKinds
{
    public const string Delete = "delete";
    public const string ClearAll = "clear_all";
    public const string ClearUser = "clear_user";
    public const string RoomState = "roomstate";
    public const string Notice = "notice";
    public const string Gap = "gap";
    public const string Dropped = "dropped";
    public const string Truncated = "truncated";
    public const string End = "end";
    public const string Purged = "purged";

    /// <summary>Kinds that carry removed text; the purge drops them once applied.</summary>
    public static bool IsTombstone(string kind) => kind is Delete or ClearUser or ClearAll;
}

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

/// <summary>
/// A stretch with no chat. <c>From</c>/<c>To</c> are media seconds; <c>Seconds</c> is the
/// wall-clock length, which differs for the zero-width media point between two parts.
/// </summary>
public sealed record ChatQueryGap(double From, double To, double Seconds);

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
