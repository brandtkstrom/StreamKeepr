using System.Text;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Records;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Captures one active session's chat into the record store: messages (deduplicated by id) and
/// other events are buffered and written in batches, when a batch fills, on
/// <see cref="Flush"/>, and on close. It enforces the per-session byte cap and, on finalize,
/// runs the opt-in purge. A failed write is logged and its batch skipped (ADR-0014); it never
/// throws to capture. All methods are serialized by its own lock.
/// </summary>
internal sealed class ChatSessionWriter(
    RecordingSummary summary,
    string source,
    ChatOptions options,
    ChatRecords records,
    int batchSize,
    TimeProvider clock,
    ILogger logger)
{
    /// <summary>The <c>Transport</c> recorded on the chat row; both sources speak IRC over WebSocket.</summary>
    private const string Transport = "wss";

    /// <summary>Twitch rarely resends an id far apart; this bounds the dedupe set's memory.</summary>
    private const int RecentIdWindow = 4096;

    /// <summary>Per-row overhead in the stored-bytes estimate: ids, times and flags.</summary>
    private const int RowOverheadBytes = 32;

    private readonly object _gate = new();
    private readonly HashSet<string> _recentIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentOrder = new();
    private readonly List<ChatMessageRow> _pendingMessages = [];
    private readonly List<ChatEventRow> _pendingEvents = [];
    private long _pendingBytes;
    private bool _pendingTruncated;
    private bool _closed;
    private bool _truncated;
    private int _messageCount;
    private long _lastMessageAt;
    private long _approxBytes;

    public string Channel => summary.Channel;

    public void Append(ChatSourceEvent evt)
    {
        lock (_gate)
        {
            if (_closed || _truncated)
                return;

            if (_approxBytes >= options.MaxBytesPerSession)
            {
                Truncate();
                return;
            }

            if (evt is ChatSourceEvent.MessageEvent message)
            {
                AppendMessage(message.Message);
            }
            else if (ToEvent(evt) is { } row)
            {
                _pendingEvents.Add(row);
                if (evt is ChatSourceEvent.Dropped drop)
                    LogDropped(drop);
            }

            if (_pendingMessages.Count + _pendingEvents.Count >= batchSize)
                FlushLocked();
        }
    }

    /// <summary>Writes whatever is buffered; a no-op when nothing is.</summary>
    public void Flush()
    {
        lock (_gate)
            FlushLocked();
    }

    private ChatEventRow? ToEvent(ChatSourceEvent evt)
    {
        ChatEventRow row;
        switch (evt)
        {
            case ChatSourceEvent.Delete delete:
                row = NewEvent(ChatEventKinds.Delete, delete.AtMs);
                row.TargetMessageId = delete.TargetId;
                return row;

            case ChatSourceEvent.Clear clear:
                row = NewEvent(
                    clear.TargetUserId is null && clear.TargetLogin is null
                        ? ChatEventKinds.ClearAll
                        : ChatEventKinds.ClearUser,
                    clear.AtMs);
                row.TargetUserId = clear.TargetUserId;
                row.TargetLogin = clear.TargetLogin;
                return row;

            case ChatSourceEvent.RoomState room:
                row = NewEvent(ChatEventKinds.RoomState, room.AtMs);
                row.EmoteOnly = room.EmoteOnly;
                row.SubscribersOnly = room.SubscribersOnly;
                row.UniqueChat = room.UniqueChat;
                row.SlowSeconds = room.SlowSeconds;
                return row;

            case ChatSourceEvent.Notice notice:
                row = NewEvent(ChatEventKinds.Notice, notice.AtMs);
                row.Reason = notice.Text;
                return row;

            case ChatSourceEvent.Gap gap:
                row = NewEvent(ChatEventKinds.Gap, gap.AtMs);
                row.FromAt = gap.FromMs;
                row.ToAt = gap.ToMs;
                row.Reason = gap.Reason;
                return row;

            case ChatSourceEvent.Dropped dropped:
                row = NewEvent(ChatEventKinds.Dropped, dropped.AtMs);
                row.FromAt = dropped.FromMs;
                row.ToAt = dropped.ToMs;
                row.Count = dropped.Count;
                row.Reason = ChatGapReasons.QueueFull;
                return row;

            default:
                return null;
        }
    }

    /// <summary>
    /// Logged only when the range is kept. A range arriving after the session is closed or
    /// truncated is discarded with it; the truncation warning already covers that loss.
    /// </summary>
    private void LogDropped(ChatSourceEvent.Dropped dropped) =>
        logger.LogWarning(
            "Chat capture for {Channel} dropped {Count} messages between {DroppedFrom} and {DroppedTo} in session {SessionId}; the capture queue was full.",
            summary.Channel,
            dropped.Count,
            DateTimeOffset.FromUnixTimeMilliseconds(dropped.FromMs),
            DateTimeOffset.FromUnixTimeMilliseconds(dropped.ToMs),
            summary.Id);

    public void Close(bool finalize)
    {
        lock (_gate)
        {
            if (_closed)
                return;

            _closed = true;
            FlushLocked();
            if (finalize)
                Finish();

            logger.LogInformation(
                "Chat session {SessionId} for {Channel} closed with {MessageCount} messages ({Bytes} message bytes); finalized: {Finalized}.",
                summary.Id, summary.Channel, _messageCount, _approxBytes, finalize);
        }
    }

    /// <summary>Marks the chat complete and runs the opt-in purge; a session without chat is left without.</summary>
    private void Finish()
    {
        DateTimeOffset now = clock.GetUtcNow();
        try
        {
            if (!records.Finish(summary.Id, now) || options.PurgeDeletedText != ChatPurgeMode.OnFinalize)
                return;
        }
        catch (Exception ex) when (ChatRecords.IsStoreFailure(ex))
        {
            logger.LogWarning(ex, "Chat for {SessionId} could not be stored as complete.", summary.Id);
            return;
        }

        try
        {
            records.Purge(summary.Id, now);
        }
        catch (Exception ex) when (ChatRecords.IsStoreFailure(ex))
        {
            logger.LogWarning(ex, "Chat purge failed for {SessionId}; the log is unchanged and the query path still hides deleted text.",
                summary.Id);
        }
    }

    private void Truncate()
    {
        // Open-ended: nothing after the last stored message is captured for the session.
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        ChatEventRow truncated = NewEvent(ChatEventKinds.Truncated, now);
        truncated.FromAt = _lastMessageAt > 0 ? _lastMessageAt : now;
        truncated.Count = _messageCount;
        _pendingEvents.Add(truncated);
        _pendingTruncated = true;
        _truncated = true;
        FlushLocked();
        logger.LogWarning(
            "Chat for {Session} reached {Limit} bytes; capture stopped for this session.",
            summary.Id, options.MaxBytesPerSession);
    }

    /// <summary>Buffers the message unless it is a recent duplicate.</summary>
    private void AppendMessage(ChatMessage message)
    {
        if (message.Id.Length > 0 && !_recentIds.Add(message.Id))
            return;

        if (message.Id.Length > 0)
        {
            _recentOrder.Enqueue(message.Id);
            while (_recentOrder.Count > RecentIdWindow)
                _recentIds.Remove(_recentOrder.Dequeue());
        }

        ChatMessageRow row = ToRow(message);
        long bytes = EstimateBytes(row);
        _pendingMessages.Add(row);
        _pendingBytes += bytes;
        _messageCount++;
        _lastMessageAt = Math.Max(_lastMessageAt, message.AtMs);
        _approxBytes += bytes;
    }

    private ChatMessageRow ToRow(ChatMessage message) => new()
    {
        SessionId = summary.Id,
        MessageId = message.Id,
        At = message.AtMs,
        Local = message.AtIsLocal ? true : null,
        UserId = message.UserId,
        Login = message.Login,
        Display = message.DisplayName,
        Color = message.Color,
        BadgesJson = ChatRecords.SerializeBadges(
            message.Badges.Select(badge => new StoredChatBadge(badge.Set, badge.Version, badge.Info))),
        Text = message.Text,
        EmotesJson = ChatRecords.SerializeEmotes(
            message.Emotes.Select(emote => new StoredChatEmote(
                emote.Id,
                emote.Ranges.Select(range => new StoredChatRange(range.Start, range.End)).ToArray()))),
        Bits = message.Bits,
        ReplyMessageId = message.Reply?.Id,
        ReplyLogin = message.Reply?.Login,
        ReplyDisplay = message.Reply?.DisplayName,
        ReplyText = message.Reply?.Text,
        IsAction = message.Action,
    };

    /// <summary>The UTF-8 size of a row's text columns plus a fixed overhead; the cap's measure.</summary>
    private static long EstimateBytes(ChatMessageRow row) =>
        RowOverheadBytes +
        Utf8(row.MessageId) + Utf8(row.UserId) + Utf8(row.Login) + Utf8(row.Display) +
        Utf8(row.Color) + Utf8(row.BadgesJson) + Utf8(row.Text) + Utf8(row.EmotesJson) +
        Utf8(row.ReplyMessageId) + Utf8(row.ReplyLogin) + Utf8(row.ReplyDisplay) + Utf8(row.ReplyText);

    private static int Utf8(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);

    private ChatEventRow NewEvent(string kind, long at) => ChatRecords.NewEvent(summary.Id, kind, at);

    /// <summary>
    /// Writes the buffer as one batch. The buffer is cleared first, so a failed batch is logged
    /// and skipped rather than retried forever against a broken store.
    /// </summary>
    private void FlushLocked()
    {
        if (_pendingMessages.Count == 0 && _pendingEvents.Count == 0)
            return;

        var batch = new ChatBatch(
            summary.Id,
            NewSession,
            _pendingMessages.ToArray(),
            _pendingEvents.ToArray(),
            _pendingBytes,
            _lastMessageAt,
            _pendingTruncated);
        _pendingMessages.Clear();
        _pendingEvents.Clear();
        _pendingBytes = 0;
        _pendingTruncated = false;

        try
        {
            records.Write(batch);
        }
        catch (Exception ex) when (ChatRecords.IsStoreFailure(ex))
        {
            logger.LogWarning(ex,
                "Chat for {SessionId} could not be stored; {MessageCount} messages and {EventCount} events were skipped.",
                summary.Id, batch.Messages.Count, batch.Events.Count);
        }
    }

    private ChatSessionRow NewSession() => new()
    {
        SessionId = summary.Id,
        Source = source,
        Transport = Transport,
        Status = ChatStates.Recording,
        StartedAt = summary.StartedAt,
        LeadInSeconds = options.LeadInSeconds,
    };
}
