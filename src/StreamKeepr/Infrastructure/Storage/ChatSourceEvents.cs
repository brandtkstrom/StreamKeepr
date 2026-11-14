namespace StreamKeepr.Infrastructure.Storage;

public sealed record ChatBadge(string Set, string Version, string? Info);

public sealed record ChatEmoteRange(int Start, int End);

public sealed record ChatEmote(string Id, IReadOnlyList<ChatEmoteRange> Ranges);

public sealed record ChatReply(
    string Id,
    string? Login,
    string? DisplayName,
    string? Text);

/// <summary>One captured Twitch chat message. All positions are Unicode code points.</summary>
public sealed record ChatMessage(
    string Id,
    string Channel,
    long AtMs,
    bool AtIsLocal,
    string? UserId,
    string Login,
    string DisplayName,
    string? Color,
    IReadOnlyList<ChatBadge> Badges,
    IReadOnlyList<ChatEmote> Emotes,
    int? Bits,
    ChatReply? Reply,
    bool Action,
    string Text);

/// <summary>
/// One event from a chat source; the log store decides how to persist it. This is the chat
/// log's input contract, so it lives in Storage and every source depends on it (ADR 0010).
/// </summary>
public abstract record ChatSourceEvent(string Channel, long AtMs)
{
    public sealed record MessageEvent(ChatMessage Message)
        : ChatSourceEvent(Message.Channel, Message.AtMs);

    public sealed record Delete(string Channel, long AtMs, string TargetId)
        : ChatSourceEvent(Channel, AtMs);

    public sealed record Clear(
        string Channel, long AtMs, string? TargetUserId, string? TargetLogin)
        : ChatSourceEvent(Channel, AtMs);

    public sealed record RoomState(
        string Channel,
        long AtMs,
        bool EmoteOnly,
        bool SubscribersOnly,
        bool UniqueChat,
        int SlowSeconds)
        : ChatSourceEvent(Channel, AtMs);

    public sealed record Notice(string Channel, long AtMs, string? MsgId, string Text)
        : ChatSourceEvent(Channel, AtMs);

    /// <summary>The connection carrying this channel ended; capture is down until rejoin.</summary>
    public sealed record Disconnected(string Channel, long AtMs)
        : ChatSourceEvent(Channel, AtMs);

    /// <summary>The source (re)joined this channel; capture resumes from here.</summary>
    public sealed record Joined(string Channel, long AtMs)
        : ChatSourceEvent(Channel, AtMs);

    public sealed record Gap(string Channel, long AtMs, long FromMs, long ToMs, string Reason)
        : ChatSourceEvent(Channel, AtMs);

    /// <summary>Events lost because the source's capture queue was full.</summary>
    public sealed record Dropped(string Channel, long AtMs, long FromMs, long ToMs, int Count)
        : ChatSourceEvent(Channel, AtMs);
}
