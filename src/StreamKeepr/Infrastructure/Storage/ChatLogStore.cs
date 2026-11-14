using System.Collections.Concurrent;
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
/// Chat is best-effort and must never fail recording.
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

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, ChatSessionWriter> _bySession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatSessionWriter> _byChannel = new(StringComparer.Ordinal);

    public static string ChatDirectory(string sessionDirectory) =>
        Path.Combine(sessionDirectory, ChatFolderName);

    public static bool HasChat(string sessionDirectory)
    {
        string directory = ChatDirectory(sessionDirectory);
        try
        {
            if (File.Exists(Path.Combine(directory, MetaFileName)))
                return true;

            return Directory.Exists(directory)
                   && Directory.EnumerateFiles(directory, "*.jsonl").Any();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
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
        lock (this._gate)
        {
            if (this._bySession.ContainsKey(summary.Id))
                return;

            var writer = new ChatSessionWriter(
                summary,
                this.SessionDirectory(summary),
                source,
                chatOptions.Value,
                clock,
                logger);

            this._bySession[summary.Id] = writer;
            this._byChannel[summary.Channel] = writer;
        }
    }

    public void Append(string channel, ChatSourceEvent evt)
    {
        ChatSessionWriter? writer;
        lock (this._gate)
            this._byChannel.TryGetValue(channel, out writer);

        writer?.Append(evt);
    }

    public void CloseSession(string sessionId, bool finalize)
    {
        ChatSessionWriter? writer;
        lock (this._gate)
        {
            if (!this._bySession.Remove(sessionId, out writer))
                return;

            if (this._byChannel.TryGetValue(writer.Channel, out ChatSessionWriter? mapped) &&
                ReferenceEquals(mapped, writer))
            {
                this._byChannel.Remove(writer.Channel);
            }
        }

        writer.Close(finalize);
    }

    public void CloseAll(bool finalize)
    {
        ChatSessionWriter[] writers;
        lock (this._gate)
        {
            writers = this._bySession.Values.ToArray();
            this._bySession.Clear();
            this._byChannel.Clear();
        }

        foreach (ChatSessionWriter writer in writers)
            writer.Close(finalize);
    }

    internal StoredChatMeta? ReadMeta(string sessionDirectory) =>
        TryDeserialize<StoredChatMeta>(Path.Combine(
            ChatDirectory(sessionDirectory), MetaFileName));

    internal IReadOnlyList<StoredChatEvent> ReadEvents(string sessionDirectory)
    {
        string path = Path.Combine(ChatDirectory(sessionDirectory), EventsFileName);
        if (!File.Exists(path))
            return [];

        var events = new List<StoredChatEvent>();
        try
        {
            foreach (string line in ReadLines(path))
            {
                if (TryDeserializeLine<StoredChatEvent>(line) is { } evt)
                    events.Add(evt);
            }
        }
        catch (IOException)
        {
            // A concurrent writer or a deleted folder: return what was read.
        }
        catch (UnauthorizedAccessException)
        {
            // Chat is best-effort; an unreadable event log is an empty one.
        }

        return events;
    }

    public IReadOnlyList<string> BucketFiles(string sessionDirectory)
    {
        string directory = ChatDirectory(sessionDirectory);
        if (!Directory.Exists(directory))
            return [];

        try
        {
            return Directory.EnumerateFiles(directory, "*.jsonl")
                .Where(path => !string.Equals(
                    Path.GetFileName(path), EventsFileName, StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal IReadOnlyList<StoredChatMessage> ReadMessages(string bucketPath)
    {
        var messages = new List<StoredChatMessage>();
        try
        {
            foreach (string line in ReadLines(bucketPath))
            {
                if (TryDeserializeLine<StoredChatMessage>(line) is { } message)
                    messages.Add(message);
            }
        }
        catch (IOException)
        {
            // A concurrent writer or a deleted bucket: return what was read.
        }
        catch (UnauthorizedAccessException)
        {
            // Chat is best-effort; an unreadable bucket is an empty one.
        }

        return messages;
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        IEnumerable<string> lines;
        try
        {
            lines = File.ReadLines(path);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        return lines.Where(line => !string.IsNullOrWhiteSpace(line));
    }

    private static T? TryDeserialize<T>(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
                : default;
        }
        catch (JsonException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
        catch (UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static T? TryDeserializeLine<T>(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(line, Json);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>One active session writer. All methods are serialized by its own lock.</summary>
    private sealed class ChatSessionWriter(
        RecordingSummary summary,
        string sessionDirectory,
        string source,
        ChatOptions options,
        TimeProvider clock,
        ILogger logger)
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _recentIds = new(StringComparer.Ordinal);
        private readonly Queue<string> _recentOrder = new();
        private string? _bucketPath;
        private StreamWriter? _bucket;
        private StreamWriter? _events;
        private bool _closed;
        private bool _truncated;
        private int _messageCount;
        private long _lastMessageAt;
        private long _approxBytes;
        private DateTimeOffset StartedAt = clock.GetUtcNow();

        public string Channel => summary.Channel;

        public string SessionId => summary.Id;

        public void Append(ChatSourceEvent evt)
        {
            lock (this._gate)
            {
                if (this._closed || this._truncated)
                    return;

                if (this._approxBytes >= options.MaxBytesPerSession)
                {
                    this.WriteEvent("truncated", clock.GetUtcNow().ToUnixTimeMilliseconds(),
                        count: this._messageCount);
                    this._truncated = true;
                    this.CloseWriters();
                    this.WriteMeta(finalize: false);
                    return;
                }

                switch (evt)
                {
                    case ChatSourceEvent.MessageEvent message:
                        this.AppendMessage(message.Message);
                        break;
                    case ChatSourceEvent.Delete delete:
                        this.WriteEvent("delete", delete.AtMs, targetId: delete.TargetId);
                        break;
                    case ChatSourceEvent.Clear clear:
                        this.WriteEvent(
                            clear.TargetUserId is null && clear.TargetLogin is null
                                ? "clear_all"
                                : "clear_user",
                            clear.AtMs,
                            targetUid: clear.TargetUserId,
                            targetLogin: clear.TargetLogin);
                        break;
                    case ChatSourceEvent.RoomState room:
                        this.WriteEvent(
                            "roomstate",
                            room.AtMs,
                            emoteOnly: room.EmoteOnly,
                            subsOnly: room.SubscribersOnly,
                            uniqueChat: room.UniqueChat,
                            slow: room.SlowSeconds);
                        break;
                    case ChatSourceEvent.Notice notice:
                        this.WriteEvent("notice", notice.AtMs, reason: notice.Text);
                        break;
                    case ChatSourceEvent.Reconnect reconnect:
                        this.WriteEvent("reconnect", reconnect.AtMs);
                        break;
                    case ChatSourceEvent.Gap gap:
                        this.WriteEvent(
                            "gap", gap.AtMs, from: gap.FromMs, to: gap.ToMs,
                            reason: gap.Reason);
                        break;
                }
            }
        }

        public void Close(bool finalize)
        {
            lock (this._gate)
            {
                if (this._closed)
                    return;

                this._closed = true;
                string eventsPath = Path.Combine(
                    ChatDirectory(sessionDirectory), EventsFileName);
                bool hasLog = this._messageCount > 0 || this._bucketPath is not null ||
                              File.Exists(eventsPath);
                if (!Directory.Exists(sessionDirectory))
                {
                    this.CloseWriters();
                    return;
                }

                if (!hasLog)
                    return;

                if (finalize)
                {
                    this.WriteEvent("end", clock.GetUtcNow().ToUnixTimeMilliseconds());
                }

                this.CloseWriters();
                this.WriteMeta(finalize);

                if (finalize && options.PurgeDeletedText == ChatPurgeMode.OnFinalize)
                    this.PurgeDeleted();
            }
        }

        private void AppendMessage(ChatMessage message)
        {
            if (message.Id.Length > 0 && !this._recentIds.Add(message.Id))
                return;

            if (message.Id.Length > 0)
            {
                this._recentOrder.Enqueue(message.Id);
                while (this._recentOrder.Count > 4096)
                    this._recentIds.Remove(this._recentOrder.Dequeue());
            }

            string bucketPath = Path.Combine(
                ChatDirectory(sessionDirectory),
                BucketName(message.AtMs, options.BucketMinutes));
            this.EnsureBucket(bucketPath);

            var stored = new StoredChatMessage(
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

            string line = JsonSerializer.Serialize(stored, Json);
            this._bucket!.WriteLine(line);
            this._bucket.Flush();
            this._messageCount++;
            this._lastMessageAt = Math.Max(this._lastMessageAt, message.AtMs);
            this._approxBytes += Encoding.UTF8.GetByteCount(line) + 1;

            if (this._messageCount == 1 || this._messageCount % 25 == 0)
                this.WriteMeta(finalize: false);
        }

        private void WriteEvent(
            string kind,
            long at,
            string? targetId = null,
            string? targetUid = null,
            string? targetLogin = null,
            long? from = null,
            long? to = null,
            int? count = null,
            string? reason = null,
            bool? emoteOnly = null,
            bool? subsOnly = null,
            bool? uniqueChat = null,
            int? slow = null)
        {
            this.EnsureEvents();
            var stored = new StoredChatEvent(
                FormatVersion, kind, at, targetId, targetUid, targetLogin,
                from, to, count, reason, emoteOnly, subsOnly, uniqueChat, slow);
            this._events!.WriteLine(JsonSerializer.Serialize(stored, Json));
            this._events.Flush();
        }

        private void EnsureBucket(string path)
        {
            if (this._bucketPath == path && this._bucket is not null)
                return;

            this._bucket?.Dispose();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            this._bucket = new StreamWriter(stream, new UTF8Encoding(false))
            {
                NewLine = "\n",
            };
            this._bucketPath = path;
        }

        private void EnsureEvents()
        {
            if (this._events is not null)
                return;

            string path = Path.Combine(ChatDirectory(sessionDirectory), EventsFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            this._events = new StreamWriter(stream, new UTF8Encoding(false))
            {
                NewLine = "\n",
            };
        }

        private void CloseWriters()
        {
            this._bucket?.Flush();
            this._bucket?.Dispose();
            this._bucket = null;
            this._bucketPath = null;

            this._events?.Flush();
            this._events?.Dispose();
            this._events = null;
        }

        private void WriteMeta(bool finalize)
        {
            if (this._messageCount == 0 && this._bucketPath is null && !File.Exists(
                    Path.Combine(ChatDirectory(sessionDirectory), EventsFileName)))
            {
                return;
            }

            string directory = ChatDirectory(sessionDirectory);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, MetaFileName);
            string temp = path + ".tmp";
            StoredChatMeta? previous = TryDeserialize<StoredChatMeta>(path);
            var meta = new StoredChatMeta(
                FormatVersion,
                summary.Id,
                summary.Channel,
                source,
                Transport,
                finalize ? "complete" : "recording",
                previous?.StartedAt ?? summary.StartedAt,
                finalize ? clock.GetUtcNow() : previous?.EndedAt,
                options.LeadInSeconds,
                options.BucketMinutes * 60,
                this._lastMessageAt > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(this._lastMessageAt)
                    : null,
                this._messageCount,
                this._truncated,
                previous?.LastError,
                previous?.PurgedCount,
                previous?.PurgedAt,
                previous?.PurgedDeletedText);

            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(meta, Json));
                File.Move(temp, path, overwrite: true);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Chat metadata could not be written for {Session}.",
                    summary.Id);
            }
        }

        private void PurgeDeleted()
        {
            string directory = ChatDirectory(sessionDirectory);
            if (!Directory.Exists(directory))
                return;

            try
            {
                string eventsPath = Path.Combine(directory, EventsFileName);
                var events = new List<StoredChatEvent>();
                if (File.Exists(eventsPath))
                {
                    foreach (string line in File.ReadLines(eventsPath))
                    {
                        if (TryDeserializeLine<StoredChatEvent>(line) is { } evt)
                            events.Add(evt);
                    }
                }

                var deletes = events
                    .Where(e => e.Kind == "delete" && e.TargetId is not null)
                    .Select(e => e.TargetId!)
                    .ToHashSet(StringComparer.Ordinal);
                var clearAll = events.Where(e => e.Kind == "clear_all").Select(e => e.At).ToArray();
                var clearUser = events.Where(e => e.Kind == "clear_user").ToArray();
                if (deletes.Count == 0 && clearAll.Length == 0 && clearUser.Length == 0)
                    return;

                var removedIds = new HashSet<string>(StringComparer.Ordinal);
                int removed = 0;
                int redacted = 0;
                int keptTotal = 0;
                long lastAt = 0;

                foreach (string bucketPath in this.BucketFiles(directory))
                {
                    var kept = new List<StoredChatMessage>();
                    foreach (string line in File.ReadLines(bucketPath))
                    {
                        if (TryDeserializeLine<StoredChatMessage>(line) is not { } message)
                            continue;

                        bool drop = deletes.Contains(message.Id) ||
                                    clearAll.Any(at => message.At <= at) ||
                                    clearUser.Any(clear => clear.At >= message.At &&
                                        ((clear.TargetUid is not null &&
                                          string.Equals(clear.TargetUid, message.UserId,
                                              StringComparison.Ordinal)) ||
                                         (clear.TargetLogin is not null &&
                                          string.Equals(clear.TargetLogin, message.Login,
                                              StringComparison.OrdinalIgnoreCase))));
                        if (drop)
                        {
                            removedIds.Add(message.Id);
                            removed++;
                            continue;
                        }

                        kept.Add(message);
                    }

                    for (int i = 0; i < kept.Count; i++)
                    {
                        StoredChatMessage message = kept[i];
                        if (message.Reply is not null &&
                            (deletes.Contains(message.Reply.Id) ||
                             removedIds.Contains(message.Reply.Id)))
                        {
                            kept[i] = message with { Reply = null };
                            redacted++;
                        }
                    }

                    if (kept.Count == 0)
                    {
                        File.Delete(bucketPath);
                        continue;
                    }

                    string temp = bucketPath + ".tmp";
                    File.WriteAllText(temp, string.Join(
                        '\n', kept.Select(message => JsonSerializer.Serialize(message, Json))) + "\n");
                    File.Move(temp, bucketPath, overwrite: true);
                    keptTotal += kept.Count;
                    lastAt = Math.Max(lastAt, kept[^1].At);
                }

                var keptEvents = events
                    .Where(e => e.Kind is not ("delete" or "clear_user" or "clear_all"))
                    .ToList();
                keptEvents.Add(new StoredChatEvent(
                    FormatVersion, "purged", clock.GetUtcNow().ToUnixTimeMilliseconds(),
                    null, null, null, null, null, removed, $"{removed} messages, {redacted} replies",
                    null, null, null, null));
                string eventsTemp = eventsPath + ".tmp";
                File.WriteAllText(eventsTemp, string.Join(
                    '\n', keptEvents.Select(evt => JsonSerializer.Serialize(evt, Json))) + "\n");
                File.Move(eventsTemp, eventsPath, overwrite: true);

                StoredChatMeta? previous = TryDeserialize<StoredChatMeta>(
                    Path.Combine(directory, MetaFileName));
                var meta = (previous ?? new StoredChatMeta(
                    FormatVersion, summary.Id, summary.Channel, source, Transport,
                    "complete", summary.StartedAt, clock.GetUtcNow(), options.LeadInSeconds,
                    options.BucketMinutes * 60, null, 0, false, null, null, null, null))
                    with
                {
                    MessageCount = keptTotal,
                    LastMessageAt = lastAt > 0
                            ? DateTimeOffset.FromUnixTimeMilliseconds(lastAt)
                            : null,
                    PurgedCount = (previous?.PurgedCount ?? 0) + removed,
                    PurgedAt = clock.GetUtcNow(),
                    PurgedDeletedText = true,
                };
                string metaTemp = Path.Combine(directory, MetaFileName) + ".tmp";
                File.WriteAllText(metaTemp, JsonSerializer.Serialize(meta, Json));
                File.Move(metaTemp, Path.Combine(directory, MetaFileName), overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                logger.LogWarning(ex, "Chat purge failed for {Session}; rewritten buckets stay purged and the query path still hides deleted text.",
                    summary.Id);
            }
        }

        private IEnumerable<string> BucketFiles(string directory) =>
            Directory.EnumerateFiles(directory, "*.jsonl")
                .Where(path => !string.Equals(
                    Path.GetFileName(path), EventsFileName, StringComparison.Ordinal))
                .ToArray();

        private static string BucketName(long atMs, int bucketMinutes)
        {
            DateTime at = DateTimeOffset.FromUnixTimeMilliseconds(atMs).UtcDateTime;
            DateTime bucket = at.AddMinutes(-(at.Minute % bucketMinutes))
                .AddSeconds(-at.Second)
                .AddMilliseconds(-at.Millisecond);
            return bucket.ToString("yyyyMMdd'T'HHmm'Z'", CultureInfo.InvariantCulture) + ".jsonl";
        }
    }
}
