using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Records;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Reads bounded media-time windows and newest-N tails of a session's chat from the record
/// store and applies tombstones, deduplication, and the per-recording sync offset. A media
/// window becomes a wall-clock <c>At</c> range through the part playlists, so each read is one
/// indexed range query by session.
/// </summary>
public sealed class ChatReader(
    ChatLogStore store,
    IOptions<ChatOptions> options,
    ILogger<ChatReader> logger)
{
    /// <summary>The largest per-recording sync nudge, in either direction.</summary>
    public const double MaxSyncOffsetSeconds = 30;

    /// <summary>How far a tail query scans back from its playhead before giving up.</summary>
    private const double TailScanSeconds = 30 * 60;

    /// <summary>
    /// Wall-clock slack around a window's bounds, so a message at a boundary survives the
    /// millisecond rounding of the mapping; anything read beyond the window is filtered out.
    /// </summary>
    private const double BoundarySlackSeconds = 1;

    /// <summary>Rows per read when a tail or a more-chat check pages through the log.</summary>
    private const int PageSize = 500;

    public Task<ChatQueryResult> QueryAsync(
        RecordingSummary summary,
        double fromSec,
        double toSec,
        int limit,
        double syncOffsetSec,
        CancellationToken ct) =>
        QueryCoreAsync(summary, fromSec, toSec, limit, syncOffsetSec, tail: false, ct);

    /// <summary>
    /// Newest <paramref name="limit"/> messages at or before <paramref name="toSec"/>,
    /// scanning backward until the tail is full, the recording starts, or the bounded
    /// scan is exhausted.
    /// </summary>
    public Task<ChatQueryResult> QueryTailAsync(
        RecordingSummary summary,
        double toSec,
        int limit,
        double syncOffsetSec,
        CancellationToken ct) =>
        QueryCoreAsync(summary, fromSec: 0, toSec, limit, syncOffsetSec, tail: true, ct);

    private async Task<ChatQueryResult> QueryCoreAsync(
        RecordingSummary summary,
        double fromSec,
        double toSec,
        int limit,
        double syncOffsetSec,
        bool tail,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(summary);

        double duration = summary.DurationSeconds ?? double.MaxValue;
        double playhead = Math.Clamp(toSec, 0, duration);
        double emptyFrom = tail ? playhead : fromSec;
        double emptyTo = tail ? playhead : toSec;

        string directory;
        try
        {
            directory = store.SessionDirectory(summary);
        }
        catch (ArgumentException)
        {
            return Empty(
                summary, emptyFrom, emptyTo, syncOffsetSec,
                ChatStates.Unavailable, false, ChatSources.None);
        }

        ChatSessionRow? chat = await store.FindChatAsync(summary.Id, ct);
        bool hasChat = chat is not null;
        string source = chat?.Source ?? ChatSources.None;
        int leadIn = chat?.LeadInSeconds ?? options.Value.LeadInSeconds;

        ChatTimeline timeline = ChatTimeline.Build(directory, leadIn, logger);
        if (!timeline.HasParts)
        {
            string state = summary.IsRecording ? ChatStates.Starting : ChatStates.Unavailable;
            return Empty(
                summary, emptyFrom, emptyTo, syncOffsetSec, state, hasChat, source);
        }

        double from = tail
            ? Math.Max(0, playhead - TailScanSeconds)
            : Math.Clamp(fromSec, 0, duration);
        double to = tail ? playhead : Math.Clamp(toSec, from, duration);
        double offset = Math.Clamp(syncOffsetSec, -MaxSyncOffsetSeconds, MaxSyncOffsetSeconds);

        IReadOnlyList<ChatEventRow> events = hasChat
            ? await store.ReadEventsAsync(summary.Id, ct)
            : [];
        var log = new LogView(summary.Id, timeline, ChatTombstones.From(events), offset);

        List<ChatQueryMessage> messages = [];
        bool hasMoreBefore = false;
        bool hasMoreAfter = false;
        if (hasChat && WallBounds(timeline, from, to, offset) is { } wall)
        {
            if (tail)
            {
                (messages, hasMoreBefore) = await ReadTailAsync(log, wall, from, to, limit, ct);
            }
            else
            {
                (messages, hasMoreBefore, hasMoreAfter) =
                    await ReadWindowAsync(log, wall, from, to, limit, ct);
            }
        }

        if (tail)
            from = messages.Count > 0 ? messages[0].Mt : to;

        IReadOnlyList<ChatQueryGap> gaps = BuildGaps(events, timeline, from, to);
        string resultState = summary.IsRecording ? ChatStates.Recording : ChatStates.Complete;
        return new ChatQueryResult(
            summary.Id,
            resultState,
            hasChat,
            source,
            leadIn,
            Math.Round(from, 3),
            Math.Round(to, 3),
            Math.Round(offset, 3),
            messages,
            gaps,
            hasMoreBefore,
            hasMoreAfter);
    }

    /// <summary>
    /// The wall-clock <c>At</c> range that can hold messages shown in [from, to]. A message is
    /// shown at <c>max(0, mapped + offset)</c>, so a window starting at 0 holds everything mapped
    /// before it; otherwise the mapped time lies in [from - offset, to - offset]. Null when the
    /// window covers no recorded part.
    /// </summary>
    private static WallWindow? WallBounds(ChatTimeline timeline, double from, double to, double offset)
    {
        bool openStart = from <= 0;
        double rawFrom = openStart ? double.NegativeInfinity : from - offset - BoundarySlackSeconds;
        double rawTo = to - offset + BoundarySlackSeconds;
        IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> ranges = timeline.WallRanges(rawFrom, rawTo);
        if (ranges.Count == 0)
            return null;

        return new WallWindow(
            ranges.Min(range => range.From).ToUnixTimeMilliseconds(),
            ranges.Max(range => range.To).ToUnixTimeMilliseconds(),
            openStart);
    }

    private async Task<(List<ChatQueryMessage> Messages, bool HasMoreBefore, bool HasMoreAfter)>
        ReadWindowAsync(LogView log, WallWindow wall, double from, double to, int limit, CancellationToken ct)
    {
        IReadOnlyList<ChatMessageRow> rows = await store.ReadMessagesAsync(
            log.SessionId, wall.From, wall.To, newestFirst: false, after: null, int.MaxValue, ct);

        var shown = new List<(ChatMessageRow Row, double Media)>();
        bool hasMoreBefore = false;
        bool hasMoreAfter = false;
        foreach (ChatMessageRow row in rows)
        {
            if (!log.Admit(row) || log.Media(row) is not { } media)
                continue;

            if (media < from)
            {
                hasMoreBefore = true;
                continue;
            }

            if (media > to)
            {
                hasMoreAfter = true;
                continue;
            }

            shown.Add((row, media));
        }

        // Converted after the whole read, so every removed id is known before replies are redacted.
        List<ChatQueryMessage> messages = shown.Select(item => log.ToQueryMessage(item.Row, item.Media)).ToList();
        messages.Sort(CompareMessages);

        if (messages.Count > limit)
        {
            hasMoreAfter = true;
            messages.RemoveRange(limit, messages.Count - limit);
        }

        if (!hasMoreBefore && !wall.OpenStart)
            hasMoreBefore = await AnyShownAsync(log, long.MinValue, wall.From - 1, newestFirst: true, ct);

        if (!hasMoreAfter)
            hasMoreAfter = await AnyShownAsync(log, wall.To + 1, long.MaxValue, newestFirst: false, ct);

        return (messages, hasMoreBefore, hasMoreAfter);
    }

    private async Task<(List<ChatQueryMessage> Messages, bool HasMoreBefore)> ReadTailAsync(
        LogView log, WallWindow wall, double scanFrom, double to, int limit, CancellationToken ct)
    {
        // Newest first, page by page, so a busy log stops as soon as the tail is full. The
        // newest copy of a duplicated id wins, the same message set as the window's dedupe.
        var collected = new List<(ChatMessageRow Row, double Media)>();
        bool sawOlder = false;
        ChatCursor? cursor = null;
        while (collected.Count <= limit)
        {
            IReadOnlyList<ChatMessageRow> page = await store.ReadMessagesAsync(
                log.SessionId, wall.From, wall.To, newestFirst: true, cursor, PageSize, ct);
            foreach (ChatMessageRow row in page)
            {
                if (!log.Admit(row) || log.Media(row) is not { } media || media > to)
                    continue;

                if (media < scanFrom)
                {
                    sawOlder = true;
                    continue;
                }

                collected.Add((row, media));
            }

            if (page.Count < PageSize)
                break;

            cursor = new ChatCursor(page[^1].At, page[^1].Id);
        }

        var messages = new List<ChatQueryMessage>(collected.Count);
        foreach ((ChatMessageRow row, double media) in collected)
            messages.Add(log.ToQueryMessage(row, media));
        messages.Sort(CompareMessages);

        // One message beyond the limit proves that an older message exists.
        bool hasMoreBefore = sawOlder || messages.Count > limit;
        if (messages.Count > limit)
            messages.RemoveRange(0, messages.Count - limit);

        if (!hasMoreBefore && !wall.OpenStart)
            hasMoreBefore = await AnyShownAsync(log, long.MinValue, wall.From - 1, newestFirst: true, ct);

        return (messages, hasMoreBefore);
    }

    /// <summary>
    /// True when a message in the <c>At</c> range would be shown somewhere in the recording:
    /// it is not removed and falls inside a recorded part. Pages from the window outward and
    /// stops at the first one.
    /// </summary>
    private async Task<bool> AnyShownAsync(
        LogView log, long fromAt, long toAt, bool newestFirst, CancellationToken ct)
    {
        if (fromAt > toAt)
            return false;

        ChatCursor? cursor = null;
        while (true)
        {
            IReadOnlyList<ChatMessageRow> page = await store.ReadMessagesAsync(
                log.SessionId, fromAt, toAt, newestFirst, cursor, PageSize, ct);
            if (page.Any(row => log.IsShown(row)))
                return true;

            if (page.Count < PageSize)
                return false;

            cursor = new ChatCursor(page[^1].At, page[^1].Id);
        }
    }

    private static int CompareMessages(ChatQueryMessage left, ChatQueryMessage right)
    {
        int byTime = left.Mt.CompareTo(right.Mt);
        return byTime != 0
            ? byTime
            : string.CompareOrdinal(left.Id, right.Id);
    }

    private static IReadOnlyList<ChatQueryGap> BuildGaps(
        IReadOnlyList<ChatEventRow> events,
        ChatTimeline timeline,
        double from,
        double to)
    {
        var gaps = new List<ChatQueryGap>();
        foreach (ChatEventRow gap in events)
        {
            (double? gapFrom, double? gapTo, double seconds) = gap.Kind switch
            {
                ChatEventKinds.Gap or ChatEventKinds.Dropped when gap.FromAt is { } start &&
                                                                  gap.ToAt is { } end =>
                    (timeline.MapGapEndToMedia(start), timeline.MapGapEndToMedia(end),
                        (end - start) / 1000d),

                // A truncated log is open-ended: nothing after the cap was captured.
                ChatEventKinds.Truncated =>
                    (timeline.MapGapEndToMedia(gap.FromAt ?? gap.At), to, double.NaN),

                _ => (null, null, 0),
            };
            AddGap(gapFrom, gapTo, seconds);
        }

        foreach ((double mediaAt, double seconds) in timeline.PartGaps())
            AddGap(mediaAt, mediaAt, seconds);

        void AddGap(double? gapFrom, double? gapTo, double seconds)
        {
            if (gapFrom is null || gapTo is null || gapTo < gapFrom ||
                gapTo < from || gapFrom > to)
            {
                return;
            }

            double start = Math.Max(from, gapFrom.Value);
            double end = Math.Min(to, gapTo.Value);
            double length = double.IsNaN(seconds) ? end - start : seconds;
            gaps.Add(new ChatQueryGap(
                Math.Round(start, 3), Math.Round(end, 3), Math.Round(length, 3)));
        }

        gaps.Sort((left, right) => left.From.CompareTo(right.From));
        return gaps;
    }

    private ChatQueryResult Empty(
        RecordingSummary summary,
        double fromSec,
        double toSec,
        double syncOffsetSec,
        string state,
        bool hasChat,
        string source) =>
        new(
            summary.Id,
            state,
            hasChat,
            source,
            options.Value.LeadInSeconds,
            fromSec,
            toSec,
            Math.Clamp(syncOffsetSec, -MaxSyncOffsetSeconds, MaxSyncOffsetSeconds),
            [],
            [],
            false,
            false);

    /// <summary>A window's <c>At</c> bounds; an open start also holds chat before the first part.</summary>
    private readonly record struct WallWindow(long From, long To, bool OpenStart);

    /// <summary>
    /// One query's view of a session's log: which rows it shows, where, and how. It remembers the
    /// ids it has admitted (to drop duplicates) and removed (to redact replies that quote them).
    /// </summary>
    private sealed class LogView(string sessionId, ChatTimeline timeline, ChatTombstones tombstones, double offset)
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly HashSet<string> _removed = new(StringComparer.Ordinal);

        public string SessionId => sessionId;

        /// <summary>
        /// False for a duplicate id, a deleted or cleared message, and a row whose text a purge
        /// removed; a removed id is remembered so replies quoting it are redacted.
        /// </summary>
        public bool Admit(ChatMessageRow row)
        {
            if (row.Text is null)
                return false;

            if (row.MessageId.Length > 0 && !_seen.Add(row.MessageId))
                return false;

            if (tombstones.Removes(row))
            {
                _removed.Add(row.MessageId);
                return false;
            }

            return true;
        }

        /// <summary>Like <see cref="Admit"/> and <see cref="Media"/>, without remembering anything.</summary>
        public bool IsShown(ChatMessageRow row) =>
            row.Text is not null && !tombstones.Removes(row) && timeline.MapToMedia(row.At) is not null;

        /// <summary>Where the row shows on the media timeline after the offset; null outside every part.</summary>
        public double? Media(ChatMessageRow row) =>
            timeline.MapToMedia(row.At) is { } mapped ? Math.Max(0, mapped + offset) : null;

        public ChatQueryMessage ToQueryMessage(ChatMessageRow row, double media) =>
            new(
                row.MessageId,
                Math.Round(media, 3),
                row.At,
                row.UserId,
                row.Login,
                row.Display,
                row.Color,
                ChatRecords.DeserializeList<StoredChatBadge>(row.BadgesJson),
                row.Text!,
                ChatRecords.DeserializeList<StoredChatEmote>(row.EmotesJson),
                row.Bits,
                row.ReplyMessageId is null || tombstones.RedactsReply(row, _removed)
                    ? null
                    : new StoredChatReply(row.ReplyMessageId, row.ReplyLogin, row.ReplyDisplay, row.ReplyText),
                row.IsAction);
    }
}
