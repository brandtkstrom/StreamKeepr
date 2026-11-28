using StreamKeepr.Infrastructure.Records;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// The one definition of which chat text a <c>delete</c>, <c>clear_user</c>, or <c>clear_all</c>
/// event removes. Query-time hiding and the finalize purge both apply it (ADR 0009).
/// </summary>
internal sealed class ChatTombstones
{
    private readonly HashSet<string> _deletes;
    private readonly long[] _clearAll;
    private readonly ChatEventRow[] _clearUser;

    private ChatTombstones(HashSet<string> deletes, long[] clearAll, ChatEventRow[] clearUser)
    {
        _deletes = deletes;
        _clearAll = clearAll;
        _clearUser = clearUser;
    }

    public bool IsEmpty => _deletes.Count == 0 && _clearAll.Length == 0 && _clearUser.Length == 0;

    public static ChatTombstones From(IEnumerable<ChatEventRow> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        ChatEventRow[] all = events.ToArray();
        return new ChatTombstones(
            all.Where(e => e.Kind == ChatEventKinds.Delete && e.TargetMessageId is not null)
                .Select(e => e.TargetMessageId!)
                .ToHashSet(StringComparer.Ordinal),
            all.Where(e => e.Kind == ChatEventKinds.ClearAll).Select(e => e.At).ToArray(),
            all.Where(e => e.Kind == ChatEventKinds.ClearUser).ToArray());
    }

    /// <summary>True when the message itself was deleted or cleared.</summary>
    public bool Removes(ChatMessageRow message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Removes(message.MessageId, message.At, message.UserId, message.Login);
    }

    /// <inheritdoc cref="Removes(ChatMessageRow)"/>
    public bool Removes(string messageId, long at, string? userId, string? login) =>
        _deletes.Contains(messageId) ||
        ClearedAll(at) ||
        _clearUser.Any(clear =>
            at <= clear.At &&
            ((clear.TargetUserId is not null &&
              string.Equals(clear.TargetUserId, userId, StringComparison.Ordinal)) ||
             TargetsLogin(clear, login)));

    /// <summary>
    /// True when the message quotes removed text: its parent was deleted or removed, or the
    /// parent's author was cleared at or after the reply (and so after the earlier parent).
    /// The parent may predate capture, so the author rule cannot rely on finding it.
    /// </summary>
    public bool RedactsReply(ChatMessageRow message, IReadOnlySet<string> removedIds)
    {
        ArgumentNullException.ThrowIfNull(message);
        return RedactsReply(message.At, message.ReplyMessageId, message.ReplyLogin, removedIds);
    }

    /// <inheritdoc cref="RedactsReply(ChatMessageRow, IReadOnlySet{string})"/>
    public bool RedactsReply(long at, string? replyId, string? replyLogin, IReadOnlySet<string> removedIds)
    {
        ArgumentNullException.ThrowIfNull(removedIds);
        if (replyId is null)
            return false;

        return _deletes.Contains(replyId) ||
               removedIds.Contains(replyId) ||
               ClearedAll(at) ||
               _clearUser.Any(clear => at <= clear.At && TargetsLogin(clear, replyLogin));
    }

    private bool ClearedAll(long at) => _clearAll.Any(clearedAt => at <= clearedAt);

    private static bool TargetsLogin(ChatEventRow clear, string? login) =>
        login is not null &&
        clear.TargetLogin is not null &&
        string.Equals(clear.TargetLogin, login, StringComparison.OrdinalIgnoreCase);
}
