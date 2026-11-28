using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Records;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Owns the per-session chat log in the record store (ADR-0014). One
/// <see cref="ChatSessionWriter"/> per active session buffers rows and writes them in batches of
/// <see cref="BatchSize"/>, whenever <see cref="Flush"/> runs (chat capture calls it every
/// second), and on close. Readers use their own contexts and never wait on a writer. Chat is
/// best-effort and must never fail recording: a write failure is logged and its batch skipped.
/// Table access lives in <see cref="ChatRecords"/>.
/// </summary>
public sealed class ChatLogStore(
    IOptions<StorageOptions> storage,
    IOptions<ChatOptions> chatOptions,
    IDbContextFactory<RecordStoreDbContext> contexts,
    TimeProvider clock,
    ILogger<ChatLogStore> logger)
{
    /// <summary>Buffered rows that trigger a write without waiting for the next flush.</summary>
    public const int BatchSize = 100;

    private readonly ChatRecords _records = new(contexts);
    private readonly object _gate = new();
    private readonly Dictionary<string, ChatSessionWriter> _bySession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatSessionWriter> _byChannel = new(StringComparer.Ordinal);

    /// <summary>The validated session directory for a summary; chat replay reads its part playlists.</summary>
    public string SessionDirectory(RecordingSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (!TwitchLogin.IsValid(summary.Channel) || !Session.IsValidId(summary.Id))
            throw new ArgumentException("The recording has an invalid channel or session id.");

        return Path.Combine(storage.Value.RecordingsRoot, summary.Channel, summary.Id);
    }

    public void OpenSession(RecordingSummary summary, string source)
    {
        // Validates the channel and id, as every chat entry point does.
        _ = SessionDirectory(summary);

        lock (_gate)
        {
            if (_bySession.ContainsKey(summary.Id))
                return;

            var writer = new ChatSessionWriter(
                summary, source, chatOptions.Value, _records, BatchSize, clock, logger);
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

    /// <summary>Writes every open session's buffered rows, so live replay trails capture by at most one flush.</summary>
    public void Flush()
    {
        ChatSessionWriter[] writers;
        lock (_gate)
            writers = _bySession.Values.ToArray();

        foreach (ChatSessionWriter writer in writers)
            writer.Flush();
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

    /// <summary>True when the session has a chat row, which is what <c>HasChat</c> means.</summary>
    public Task<bool> HasChatAsync(string sessionId, CancellationToken ct) =>
        _records.HasChatAsync(sessionId, ct);

    internal Task<ChatSessionRow?> FindChatAsync(string sessionId, CancellationToken ct) =>
        _records.FindSessionAsync(sessionId, ct);

    internal Task<IReadOnlyList<ChatEventRow>> ReadEventsAsync(string sessionId, CancellationToken ct) =>
        _records.EventsAsync(sessionId, ct);

    internal Task<IReadOnlyList<ChatMessageRow>> ReadMessagesAsync(
        string sessionId,
        long fromAt,
        long toAt,
        bool newestFirst,
        ChatCursor? after,
        int take,
        CancellationToken ct) =>
        _records.MessagesAsync(sessionId, fromAt, toAt, newestFirst, after, take, ct);
}
