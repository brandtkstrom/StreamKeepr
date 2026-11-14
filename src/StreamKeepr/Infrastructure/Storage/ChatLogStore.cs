using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Twitch.Chat;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Owns the per-session chat log. One writer per active session; readers never take a write lock.
/// Chat is best-effort and must never fail recording: read failures are logged and degrade to
/// what could be read.
/// </summary>
public sealed class ChatLogStore(
    IOptions<StorageOptions> storage,
    IOptions<ChatOptions> chatOptions,
    TimeProvider clock,
    ILogger<ChatLogStore> logger)
{
    public const int FormatVersion = 1;
    public const string ChatFolderName = "chat";
    public const string MetaFileName = "meta.json";
    public const string EventsFileName = "events.jsonl";
    public const string Transport = "wss";
    public const string AnonymousSource = "irc-anonymous";
    public const string TokenSource = "irc-token";
    public const string MockSource = "mock";

    private const string JsonLinesPattern = "*.jsonl";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, ChatSessionWriter> _bySession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatSessionWriter> _byChannel = new(StringComparer.Ordinal);

    public static string ChatDirectory(string sessionDirectory) =>
        Path.Combine(sessionDirectory, ChatFolderName);

    /// <summary>The UTC start of the bucket that holds messages sent at <paramref name="at"/>.</summary>
    public static DateTimeOffset BucketStart(DateTimeOffset at, int bucketSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bucketSeconds, 1);
        long seconds = at.ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(
            seconds - (((seconds % bucketSeconds) + bucketSeconds) % bucketSeconds));
    }

    /// <summary>The bucket file holding messages sent at <paramref name="at"/>.</summary>
    public static string BucketFileName(DateTimeOffset at, int bucketSeconds) =>
        BucketStart(at, bucketSeconds).UtcDateTime.ToString(
            "yyyyMMdd'T'HHmm'Z'", CultureInfo.InvariantCulture) + ".jsonl";

    public static bool HasChat(string sessionDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        string directory = ChatDirectory(sessionDirectory);
        try
        {
            if (File.Exists(Path.Combine(directory, MetaFileName)))
                return true;

            return Directory.Exists(directory)
                   && Directory.EnumerateFiles(directory, JsonLinesPattern).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not check {Directory} for chat; treating it as having none.",
                directory);
            return false;
        }
    }

    /// <summary>Message bucket files in time order; empty when there is no readable chat log.</summary>
    public static IReadOnlyList<string> BucketFiles(string sessionDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        string directory = ChatDirectory(sessionDirectory);
        if (!Directory.Exists(directory))
            return [];

        try
        {
            return Directory.EnumerateFiles(directory, JsonLinesPattern)
                .Where(path => !string.Equals(
                    Path.GetFileName(path), EventsFileName, StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not list chat buckets in {Directory}.", directory);
            return [];
        }
    }

    /// <summary>The validated session directory for a summary.</summary>
    public string SessionDirectory(RecordingSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (!TwitchLogin.IsValid(summary.Channel) || !Session.IsValidId(summary.Id))
            throw new ArgumentException("The recording has an invalid channel or session id.");

        return Path.Combine(storage.Value.RecordingsRoot, summary.Channel, summary.Id);
    }

    public void OpenSession(RecordingSummary summary, string source)
    {
        ArgumentNullException.ThrowIfNull(summary);
        lock (_gate)
        {
            if (_bySession.ContainsKey(summary.Id))
                return;

            var writer = new ChatSessionWriter(
                summary, SessionDirectory(summary), source, chatOptions.Value, clock, logger);
            _bySession[summary.Id] = writer;
            _byChannel[summary.Channel] = writer;
        }
    }

    public void Append(string channel, ChatSourceEvent evt)
    {
        ChatSessionWriter? writer;
        lock (_gate)
            _byChannel.TryGetValue(channel, out writer);

        writer?.Append(evt);
    }

    public void CloseSession(string sessionId, bool finalize)
    {
        ChatSessionWriter? writer;
        lock (_gate)
        {
            if (!_bySession.Remove(sessionId, out writer))
                return;

            if (_byChannel.TryGetValue(writer.Channel, out ChatSessionWriter? mapped) &&
                ReferenceEquals(mapped, writer))
            {
                _byChannel.Remove(writer.Channel);
            }
        }

        writer.Close(finalize);
    }

    public void CloseAll(bool finalize)
    {
        ChatSessionWriter[] writers;
        lock (_gate)
        {
            writers = _bySession.Values.ToArray();
            _bySession.Clear();
            _byChannel.Clear();
        }

        foreach (ChatSessionWriter writer in writers)
            writer.Close(finalize);
    }

    internal StoredChatMeta? ReadMeta(string sessionDirectory) =>
        TryReadJson<StoredChatMeta>(
            Path.Combine(ChatDirectory(sessionDirectory), MetaFileName), logger);

    internal IReadOnlyList<StoredChatEvent> ReadEvents(string sessionDirectory) =>
        ReadJsonLines<StoredChatEvent>(
            Path.Combine(ChatDirectory(sessionDirectory), EventsFileName));

    internal IReadOnlyList<StoredChatMessage> ReadMessages(string bucketPath) =>
        ReadJsonLines<StoredChatMessage>(bucketPath);

    /// <summary>Reads a live log tolerantly: a failure is logged and returns what was read.</summary>
    private List<T> ReadJsonLines<T>(string path)
    {
        var items = new List<T>();
        if (!File.Exists(path))
            return items;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            items.AddRange(ParseLines<T>(ReadLines(reader), path, logger));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Chat log {Path} could not be fully read; using {Count} entries.",
                path, items.Count);
        }

        return items;
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    /// <summary>
    /// Parses JSON lines, skipping malformed ones. A torn last line is expected while a writer
    /// is mid-append, so a skip is logged at debug level only.
    /// </summary>
    private static IEnumerable<T> ParseLines<T>(IEnumerable<string> lines, string path, ILogger logger)
    {
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            T? item;
            try
            {
                item = JsonSerializer.Deserialize<T>(line, Json);
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "Skipping a malformed chat log line in {Path}.", path);
                continue;
            }

            if (item is not null)
                yield return item;
        }
    }

    private static T? TryReadJson<T>(string path, ILogger logger)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
                : default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Chat metadata {Path} could not be read.", path);
            return default;
        }
    }

    /// <summary>Writes through a sibling temp file, the storage convention, so readers never see a partial file.</summary>
    private static void WriteAllTextAtomically(string path, string content)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static string ToJsonLines<T>(IEnumerable<T> items) =>
        string.Join('\n', items.Select(item => JsonSerializer.Serialize(item, Json))) + "\n";

    private static StoredChatEvent Event(string kind, long at) =>
        new(FormatVersion, kind, at, null, null, null, null, null, null, null, null, null, null, null);

    /// <summary>One active session writer. All methods are serialized by its own lock.</summary>
    private sealed class ChatSessionWriter(
        RecordingSummary summary,
        string sessionDirectory,
        string source,
        ChatOptions options,
        TimeProvider clock,
        ILogger logger)
    {
        /// <summary>Twitch rarely resends an id far apart; this bounds the dedupe set's memory.</summary>
        private const int RecentIdWindow = 4096;

        /// <summary>Meta is advisory; refreshing it every N messages keeps rewrites cheap.</summary>
        private const int MetaRefreshEveryMessages = 25;

        private readonly object _gate = new();
        private readonly HashSet<string> _recentIds = new(StringComparer.Ordinal);
        private readonly Queue<string> _recentOrder = new();
        private readonly string _directory = ChatDirectory(sessionDirectory);
        private string? _bucketPath;
        private StreamWriter? _bucket;
        private StreamWriter? _events;
        private bool _closed;
        private bool _truncated;
        private int _messageCount;
        private long _lastMessageAt;
        private long _approxBytes;

        public string Channel => summary.Channel;

        private string EventsPath => Path.Combine(_directory, EventsFileName);

        private string MetaPath => Path.Combine(_directory, MetaFileName);

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
                        Event(ChatEventKinds.Delete, delete.AtMs) with { TargetId = delete.TargetId },
                    ChatSourceEvent.Clear clear =>
                        Event(clear.TargetUserId is null && clear.TargetLogin is null
                                ? ChatEventKinds.ClearAll
                                : ChatEventKinds.ClearUser,
                            clear.AtMs) with
                        {
                            TargetUid = clear.TargetUserId,
                            TargetLogin = clear.TargetLogin,
                        },
                    ChatSourceEvent.RoomState room =>
                        Event(ChatEventKinds.RoomState, room.AtMs) with
                        {
                            EmoteOnly = room.EmoteOnly,
                            SubscribersOnly = room.SubscribersOnly,
                            UniqueChat = room.UniqueChat,
                            Slow = room.SlowSeconds,
                        },
                    ChatSourceEvent.Notice notice =>
                        Event(ChatEventKinds.Notice, notice.AtMs) with { Reason = notice.Text },
                    ChatSourceEvent.Gap gap =>
                        Event(ChatEventKinds.Gap, gap.AtMs) with
                        {
                            From = gap.FromMs,
                            To = gap.ToMs,
                            Reason = gap.Reason,
                        },
                    ChatSourceEvent.Dropped dropped =>
                        Event(ChatEventKinds.Dropped, dropped.AtMs) with
                        {
                            From = dropped.FromMs,
                            To = dropped.ToMs,
                            Count = dropped.Count,
                            Reason = "queue-full",
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
                    WriteEvent(Event(ChatEventKinds.End, clock.GetUtcNow().ToUnixTimeMilliseconds()));

                CloseWriters();
                WriteMeta(finalize);

                if (finalize && options.PurgeDeletedText == ChatPurgeMode.OnFinalize)
                    PurgeDeleted();
            }
        }

        private bool HasLog => _messageCount > 0 || _bucketPath is not null || File.Exists(EventsPath);

        private void Truncate()
        {
            // Open-ended: nothing after the last stored message is captured for the session.
            long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            WriteEvent(Event(ChatEventKinds.Truncated, now) with
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

            string bucketPath = Path.Combine(_directory, BucketFileName(
                DateTimeOffset.FromUnixTimeMilliseconds(message.AtMs), options.BucketMinutes * 60));
            if (_bucketPath != bucketPath || _bucket is null)
            {
                _bucket?.Dispose();
                _bucket = OpenAppend(bucketPath);
                _bucketPath = bucketPath;
            }

            string line = JsonSerializer.Serialize(ToStored(message), Json);
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
                FormatVersion,
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
            _events ??= OpenAppend(EventsPath);
            _events.WriteLine(JsonSerializer.Serialize(evt, Json));
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
                FormatVersion,
                summary.Id,
                summary.Channel,
                source,
                Transport,
                finalize ? ChatStates.Complete : ChatStates.Recording,
                previous?.StartedAt ?? summary.StartedAt,
                finalize ? clock.GetUtcNow() : previous?.EndedAt,
                options.LeadInSeconds,
                options.BucketMinutes * 60,
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
                StoredChatMeta meta = BuildMeta(finalize, TryReadJson<StoredChatMeta>(MetaPath, logger));
                WriteAllTextAtomically(MetaPath, JsonSerializer.Serialize(meta, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Chat metadata could not be written for {Session}.", summary.Id);
            }
        }

        private void PurgeDeleted()
        {
            if (!Directory.Exists(_directory))
                return;

            try
            {
                // Strict reads: a bucket that cannot be read fully must not be rewritten.
                List<StoredChatEvent> events = File.Exists(EventsPath)
                    ? ParseLines<StoredChatEvent>(File.ReadLines(EventsPath), EventsPath, logger).ToList()
                    : [];
                ChatTombstones tombstones = ChatTombstones.From(events);
                if (tombstones.IsEmpty)
                    return;

                // Pass 1 finds every removed message, so a reply in any bucket can be redacted
                // regardless of the order buckets are rewritten in.
                var buckets = new List<(string Path, List<StoredChatMessage> Kept)>();
                var removedIds = new HashSet<string>(StringComparer.Ordinal);
                int removed = 0;
                foreach (string bucketPath in BucketFiles(sessionDirectory, logger))
                {
                    var kept = new List<StoredChatMessage>();
                    foreach (StoredChatMessage message in ParseLines<StoredChatMessage>(
                                 File.ReadLines(bucketPath), bucketPath, logger))
                    {
                        if (tombstones.Removes(message))
                        {
                            removedIds.Add(message.Id);
                            removed++;
                        }
                        else
                        {
                            kept.Add(message);
                        }
                    }

                    buckets.Add((bucketPath, kept));
                }

                int redacted = 0;
                int keptTotal = 0;
                long lastAt = 0;
                foreach ((string bucketPath, List<StoredChatMessage> kept) in buckets)
                {
                    for (int i = 0; i < kept.Count; i++)
                    {
                        if (tombstones.RedactsReply(kept[i], removedIds))
                        {
                            kept[i] = kept[i] with { Reply = null };
                            redacted++;
                        }
                    }

                    if (kept.Count == 0)
                    {
                        File.Delete(bucketPath);
                        continue;
                    }

                    WriteAllTextAtomically(bucketPath, ToJsonLines(kept));
                    keptTotal += kept.Count;
                    lastAt = Math.Max(lastAt, kept[^1].At);
                }

                List<StoredChatEvent> keptEvents = events
                    .Where(e => !ChatEventKinds.IsTombstone(e.Kind))
                    .Append(Event(ChatEventKinds.Purged, clock.GetUtcNow().ToUnixTimeMilliseconds()) with
                    {
                        Count = removed,
                        Reason = $"{removed} messages, {redacted} replies",
                    })
                    .ToList();
                WriteAllTextAtomically(EventsPath, ToJsonLines(keptEvents));

                StoredChatMeta? previous = TryReadJson<StoredChatMeta>(MetaPath, logger);
                StoredChatMeta meta = (previous ?? BuildMeta(finalize: true, previous: null)) with
                {
                    MessageCount = keptTotal,
                    LastMessageAt = lastAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(lastAt) : null,
                    PurgedCount = (previous?.PurgedCount ?? 0) + removed,
                    PurgedAt = clock.GetUtcNow(),
                    PurgedDeletedText = true,
                };
                WriteAllTextAtomically(MetaPath, JsonSerializer.Serialize(meta, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                logger.LogWarning(ex, "Chat purge failed for {Session}; rewritten buckets stay purged and the query path still hides deleted text.",
                    summary.Id);
            }
        }
    }
}
