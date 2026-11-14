using System.Text;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Twitch.Chat;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Appends one active session's chat log: messages to their time bucket (deduplicated by
/// id), other events to <c>events.jsonl</c>, and advisory <c>meta.json</c> refreshes. It
/// enforces the per-session byte cap and, on finalize, runs the opt-in purge. All methods
/// are serialized by its own lock.
/// </summary>
internal sealed class ChatSessionWriter(
    RecordingSummary summary,
    string sessionDirectory,
    string source,
    ChatOptions options,
    TimeProvider clock,
    ILogger logger)
{
    /// <summary>The <c>transport</c> recorded in <c>meta.json</c>; both sources speak IRC over WebSocket.</summary>
    private const string Transport = "wss";

    /// <summary>Twitch rarely resends an id far apart; this bounds the dedupe set's memory.</summary>
    private const int RecentIdWindow = 4096;

    /// <summary>Meta is advisory; refreshing it every N messages keeps rewrites cheap.</summary>
    private const int MetaRefreshEveryMessages = 25;

    private readonly object _gate = new();
    private readonly HashSet<string> _recentIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentOrder = new();
    private readonly string _directory = ChatLogLayout.ChatDirectory(sessionDirectory);
    private readonly string _eventsPath = ChatLogLayout.EventsPath(sessionDirectory);
    private readonly string _metaPath = ChatLogLayout.MetaPath(sessionDirectory);
    private string? _bucketPath;
    private StreamWriter? _bucket;
    private StreamWriter? _events;
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
                return;
            }

            StoredChatEvent? stored = evt switch
            {
                ChatSourceEvent.Delete delete =>
                    StoredChatEvent.Create(ChatEventKinds.Delete, delete.AtMs) with { TargetId = delete.TargetId },
                ChatSourceEvent.Clear clear =>
                    StoredChatEvent.Create(clear.TargetUserId is null && clear.TargetLogin is null
                            ? ChatEventKinds.ClearAll
                            : ChatEventKinds.ClearUser,
                        clear.AtMs) with
                    {
                        TargetUid = clear.TargetUserId,
                        TargetLogin = clear.TargetLogin,
                    },
                ChatSourceEvent.RoomState room =>
                    StoredChatEvent.Create(ChatEventKinds.RoomState, room.AtMs) with
                    {
                        EmoteOnly = room.EmoteOnly,
                        SubscribersOnly = room.SubscribersOnly,
                        UniqueChat = room.UniqueChat,
                        Slow = room.SlowSeconds,
                    },
                ChatSourceEvent.Notice notice =>
                    StoredChatEvent.Create(ChatEventKinds.Notice, notice.AtMs) with { Reason = notice.Text },
                ChatSourceEvent.Gap gap =>
                    StoredChatEvent.Create(ChatEventKinds.Gap, gap.AtMs) with
                    {
                        From = gap.FromMs,
                        To = gap.ToMs,
                        Reason = gap.Reason,
                    },
                ChatSourceEvent.Dropped dropped =>
                    StoredChatEvent.Create(ChatEventKinds.Dropped, dropped.AtMs) with
                    {
                        From = dropped.FromMs,
                        To = dropped.ToMs,
                        Count = dropped.Count,
                        Reason = ChatGapReasons.QueueFull,
                    },
                _ => null,
            };

            if (stored is not null)
                WriteEvent(stored);
        }
    }

    public void Close(bool finalize)
    {
        lock (_gate)
        {
            if (_closed)
                return;

            _closed = true;
            if (!Directory.Exists(sessionDirectory))
            {
                CloseWriters();
                return;
            }

            if (!HasLog)
                return;

            if (finalize)
                WriteEvent(StoredChatEvent.Create(ChatEventKinds.End, clock.GetUtcNow().ToUnixTimeMilliseconds()));

            CloseWriters();
            WriteMeta(finalize);

            if (finalize && options.PurgeDeletedText == ChatPurgeMode.OnFinalize)
            {
                ChatLogPurge.PurgeDeleted(
                    sessionDirectory,
                    summary.Id,
                    () => BuildMeta(finalize: true, previous: null),
                    clock,
                    logger);
            }
        }
    }

    private bool HasLog => _messageCount > 0 || _bucketPath is not null || File.Exists(_eventsPath);

    private void Truncate()
    {
        // Open-ended: nothing after the last stored message is captured for the session.
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        WriteEvent(StoredChatEvent.Create(ChatEventKinds.Truncated, now) with
        {
            From = _lastMessageAt > 0 ? _lastMessageAt : now,
            Count = _messageCount,
        });
        _truncated = true;
        CloseWriters();
        WriteMeta(finalize: false);
        logger.LogWarning(
            "Chat for {Session} reached {Limit} bytes; capture stopped for this session.",
            summary.Id, options.MaxBytesPerSession);
    }

    /// <summary>Writes the message to its time bucket unless it is a recent duplicate.</summary>
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

        string bucketPath = Path.Combine(_directory, ChatLogLayout.BucketFileName(
            DateTimeOffset.FromUnixTimeMilliseconds(message.AtMs), options.BucketSeconds));
        if (_bucketPath != bucketPath || _bucket is null)
        {
            _bucket?.Dispose();
            _bucket = OpenAppend(bucketPath);
            _bucketPath = bucketPath;
        }

        string line = ChatLogJson.Serialize(ToStored(message));
        _bucket.WriteLine(line);
        _bucket.Flush();
        _messageCount++;
        _lastMessageAt = Math.Max(_lastMessageAt, message.AtMs);
        _approxBytes += Encoding.UTF8.GetByteCount(line) + 1;

        if (_messageCount == 1 || _messageCount % MetaRefreshEveryMessages == 0)
            WriteMeta(finalize: false);
    }

    private static StoredChatMessage ToStored(ChatMessage message) =>
        new(
            ChatLogFormat.Version,
            message.Id,
            message.AtMs,
            message.AtIsLocal ? true : null,
            message.UserId,
            message.Login,
            message.DisplayName,
            message.Color,
            message.Badges.Select(badge => new StoredChatBadge(
                badge.Set, badge.Version, badge.Info)).ToArray(),
            message.Text,
            message.Emotes.Select(emote => new StoredChatEmote(
                emote.Id,
                emote.Ranges.Select(range => new StoredChatRange(
                    range.Start, range.End)).ToArray())).ToArray(),
            message.Bits,
            message.Reply is null
                ? null
                : new StoredChatReply(
                    message.Reply.Id,
                    message.Reply.Login,
                    message.Reply.DisplayName,
                    message.Reply.Text),
            message.Action);

    private void WriteEvent(StoredChatEvent evt)
    {
        _events ??= OpenAppend(_eventsPath);
        _events.WriteLine(ChatLogJson.Serialize(evt));
        _events.Flush();
    }

    private static StreamWriter OpenAppend(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
    }

    private void CloseWriters()
    {
        _bucket?.Dispose();
        _bucket = null;
        _bucketPath = null;

        _events?.Dispose();
        _events = null;
    }

    private StoredChatMeta BuildMeta(bool finalize, StoredChatMeta? previous) =>
        new(
            ChatLogFormat.Version,
            summary.Id,
            summary.Channel,
            source,
            Transport,
            finalize ? ChatStates.Complete : ChatStates.Recording,
            previous?.StartedAt ?? summary.StartedAt,
            finalize ? clock.GetUtcNow() : previous?.EndedAt,
            options.LeadInSeconds,
            options.BucketSeconds,
            _lastMessageAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(_lastMessageAt) : null,
            _messageCount,
            _truncated,
            previous?.LastError,
            previous?.PurgedCount,
            previous?.PurgedAt,
            previous?.PurgedDeletedText);

    private void WriteMeta(bool finalize)
    {
        if (!HasLog)
            return;

        try
        {
            Directory.CreateDirectory(_directory);
            StoredChatMeta meta = BuildMeta(finalize, ChatLogJson.TryRead<StoredChatMeta>(_metaPath, logger));
            ChatLogJson.WriteAllTextAtomically(_metaPath, ChatLogJson.Serialize(meta));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Chat metadata could not be written for {Session}.", summary.Id);
        }
    }
}
