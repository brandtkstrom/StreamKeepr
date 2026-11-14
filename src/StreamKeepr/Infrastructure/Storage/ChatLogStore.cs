using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Owns the per-session chat log. One <see cref="ChatSessionWriter"/> per active session;
/// readers never take a write lock. Chat is best-effort and must never fail recording: read
/// failures are logged and degrade to what could be read. File naming lives in
/// <see cref="ChatLogLayout"/>, JSON I/O in <see cref="ChatLogJson"/>, and the finalize purge in
/// <see cref="ChatLogPurge"/>.
/// </summary>
public sealed class ChatLogStore(
    IOptions<StorageOptions> storage,
    IOptions<ChatOptions> chatOptions,
    TimeProvider clock,
    ILogger<ChatLogStore> logger)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ChatSessionWriter> _bySession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatSessionWriter> _byChannel = new(StringComparer.Ordinal);

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
        ChatLogJson.TryRead<StoredChatMeta>(ChatLogLayout.MetaPath(sessionDirectory), logger);

    internal IReadOnlyList<StoredChatEvent> ReadEvents(string sessionDirectory) =>
        ChatLogJson.ReadLinesTolerant<StoredChatEvent>(ChatLogLayout.EventsPath(sessionDirectory), logger);

    internal IReadOnlyList<StoredChatMessage> ReadMessages(string bucketPath) =>
        ChatLogJson.ReadLinesTolerant<StoredChatMessage>(bucketPath, logger);
}
