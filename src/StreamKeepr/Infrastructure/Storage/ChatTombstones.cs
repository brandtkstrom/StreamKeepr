namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// The one definition of which chat text a <c>delete</c>, <c>clear_user</c>, or <c>clear_all</c>
/// event removes. Query-time hiding and the finalize purge both apply it (ADR 0009).
/// </summary>
internal sealed class ChatTombstones
{
    private readonly HashSet<string> _deletes;
    private readonly long[] _clearAll;
    private readonly StoredChatEvent[] _clearUser;

    private ChatTombstones(HashSet<string> deletes, long[] clearAll, StoredChatEvent[] clearUser)
    {
        _deletes = deletes;
        _clearAll = clearAll;
        _clearUser = clearUser;
    }

    public bool IsEmpty => _deletes.Count == 0 && _clearAll.Length == 0 && _clearUser.Length == 0;

    public static ChatTombstones From(IEnumerable<StoredChatEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        StoredChatEvent[] all = events.ToArray();
        return new ChatTombstones(
            all.Where(e => e.Kind == ChatEventKinds.Delete && e.TargetId is not null)
                .Select(e => e.TargetId!)
                .ToHashSet(StringComparer.Ordinal),
            all.Where(e => e.Kind == ChatEventKinds.ClearAll).Select(e => e.At).ToArray(),
            all.Where(e => e.Kind == ChatEventKinds.ClearUser).ToArray());
    }

    /// <summary>True when the message itself was deleted or cleared.</summary>
    public bool Removes(StoredChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _deletes.Contains(message.Id) ||
               ClearedAll(message.At) ||
               _clearUser.Any(clear =>
                   message.At <= clear.At &&
                   ((clear.TargetUid is not null &&
                     string.Equals(clear.TargetUid, message.UserId, StringComparison.Ordinal)) ||
                    TargetsLogin(clear, message.Login)));
    }

    /// <summary>
    /// True when the message quotes removed text: its parent was deleted or removed, or the
    /// parent's author was cleared at or after the reply (and so after the earlier parent).
    /// The parent may predate capture, so the author rule cannot rely on finding it.
    /// </summary>
    public bool RedactsReply(StoredChatMessage message, IReadOnlySet<string> removedIds)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(removedIds);
        if (message.Reply is not { } reply)
            return false;

        return _deletes.Contains(reply.Id) ||
               removedIds.Contains(reply.Id) ||
               ClearedAll(message.At) ||
               _clearUser.Any(clear => message.At <= clear.At && TargetsLogin(clear, reply.Login));
    }

    private bool ClearedAll(long at) => _clearAll.Any(clearedAt => at <= clearedAt);

    private static bool TargetsLogin(StoredChatEvent clear, string? login) =>
        login is not null &&
        clear.TargetLogin is not null &&
        string.Equals(clear.TargetLogin, login, StringComparison.OrdinalIgnoreCase);
}
