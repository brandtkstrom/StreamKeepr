using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Reads bounded media-time windows and newest-N tails from the per-session chat log and
/// applies tombstones, deduplication, and the per-recording sync offset.
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

    /// <summary>The most bucket files a single tail query opens.</summary>
    private const int MaxTailBuckets = 100;

    public ChatQueryResult Query(
        RecordingSummary summary,
        double fromSec,
        double toSec,
        int limit,
        double syncOffsetSec) =>
        QueryCore(summary, fromSec, toSec, limit, syncOffsetSec, tail: false);


    /// <summary>
    /// Newest <paramref name="limit"/> messages at or before <paramref name="toSec"/>,
    /// scanning backward until the tail is full, the recording starts, or the bounded
    /// scan is exhausted.
    /// </summary>
    public ChatQueryResult QueryTail(
        RecordingSummary summary,
        double toSec,
        int limit,
        double syncOffsetSec) =>
        QueryCore(summary, fromSec: 0, toSec, limit, syncOffsetSec, tail: true);

    private ChatQueryResult QueryCore(
        RecordingSummary summary,
        double fromSec,
        double toSec,
        int limit,
        double syncOffsetSec,
        bool tail)
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

        StoredChatMeta? meta = store.ReadMeta(directory);
        bool hasChat = ChatLogLayout.HasChat(directory, logger);
        string source = meta?.Source ?? ChatSources.None;
        int leadIn = meta?.LeadInSec ?? options.Value.LeadInSeconds;
        int bucketSeconds = meta?.BucketSeconds is > 0
            ? meta.BucketSeconds
            : options.Value.BucketSeconds;

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

        IReadOnlyList<StoredChatEvent> events = store.ReadEvents(directory);
        ChatTombstones tombstones = ChatTombstones.From(events);
        var removedIds = new HashSet<string>(StringComparer.Ordinal);

        List<ChatQueryMessage> messages;
        bool hasMoreBefore;
        bool hasMoreAfter;
        if (tail)
        {
            (messages, hasMoreBefore) = ReadTail(
                directory, timeline, tombstones, removedIds, from, to, offset, limit,
                bucketSeconds);
            hasMoreAfter = false;
            from = messages.Count > 0 ? messages[0].Mt : to;
        }
        else
        {
            (messages, hasMoreBefore, hasMoreAfter) = ReadWindow(
                directory, timeline, tombstones, removedIds, from, to, offset, limit,
                bucketSeconds);
        }

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

    private IReadOnlyList<string> BucketFiles(
        string directory, ChatTimeline timeline, double from, double to, int bucketSeconds)
    {
        HashSet<string> names = BucketNames(timeline, from, to, bucketSeconds);
        return ChatLogLayout.BucketFiles(directory, logger)
            .Where(path => names.Contains(Path.GetFileName(path)))
            .ToArray();
    }

    private static HashSet<string> BucketNames(
        ChatTimeline timeline, double from, double to, int bucketSeconds)
    {
        // One bucket of slack on each side covers messages near a boundary and the sync offset.
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach ((DateTimeOffset rangeFrom, DateTimeOffset rangeTo) in timeline.WallRanges(from, to))
        {
            DateTimeOffset start = ChatLogLayout.BucketStart(rangeFrom, bucketSeconds)
                .AddSeconds(-bucketSeconds);
            DateTimeOffset end = ChatLogLayout.BucketStart(rangeTo, bucketSeconds)
                .AddSeconds(bucketSeconds);
            for (DateTimeOffset bucket = start; bucket <= end;
                 bucket = bucket.AddSeconds(bucketSeconds))
            {
                names.Add(ChatLogLayout.BucketFileName(bucket, bucketSeconds));
            }
        }

        return names;
    }


    private (List<ChatQueryMessage> Messages, bool HasMoreBefore, bool HasMoreAfter)
        ReadWindow(
            string directory,
            ChatTimeline timeline,
            ChatTombstones tombstones,
            HashSet<string> removedIds,
            double from,
            double to,
            double offset,
            int limit,
            int bucketSeconds)
    {
        var candidates = new List<StoredChatMessage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string bucketPath in BucketFiles(directory, timeline, from, to, bucketSeconds))
        {
            foreach (StoredChatMessage message in store.ReadMessages(bucketPath))
            {
                if (!string.IsNullOrEmpty(message.Id) && !seen.Add(message.Id))
                    continue;

                if (tombstones.Removes(message))
                    removedIds.Add(message.Id);
                else
                    candidates.Add(message);
            }
        }

        var messages = new List<ChatQueryMessage>();
        bool hasMoreBefore = false;
        bool hasMoreAfter = false;
        foreach (StoredChatMessage message in candidates)
        {
            double? mapped = timeline.MapToMedia(message.At);
            if (mapped is null)
                continue;

            double media = Math.Max(0, mapped.Value + offset);
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

            messages.Add(ToQueryMessage(message, media, tombstones, removedIds));
        }

        messages.Sort(CompareMessages);

        if (messages.Count > limit)
        {
            hasMoreAfter = true;
            messages.RemoveRange(limit, messages.Count - limit);
        }

        return (messages, hasMoreBefore, hasMoreAfter);
    }

    private (List<ChatQueryMessage> Messages, bool HasMoreBefore) ReadTail(
        string directory,
        ChatTimeline timeline,
        ChatTombstones tombstones,
        HashSet<string> removedIds,
        double scanFrom,
        double to,
        double offset,
        int limit,
        int bucketSeconds)
    {
        // The scan is expressed in media time, but buckets are named by wall clock.
        // Expand by the sync offset, and by one second so messages at the playhead
        // boundary are not lost, before selecting the bucket files.
        double rawFrom = Math.Max(0, scanFrom - offset);
        double rawTo = to - offset + 1;
        (IReadOnlyList<string> buckets, bool capped) =
            TailBuckets(directory, timeline, rawFrom, rawTo, bucketSeconds);

        var collected = new List<(StoredChatMessage Message, double Media)>();
        // The writer emits identical rows for one id, so collapsing on the newest copy
        // produces the same message set as the window query's oldest-first dedupe.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool sawOlder = false;
        foreach (string bucketPath in buckets)
        {
            foreach (StoredChatMessage message in store.ReadMessages(bucketPath))
            {
                if (!string.IsNullOrEmpty(message.Id) && !seen.Add(message.Id))
                    continue;

                if (tombstones.Removes(message))
                {
                    removedIds.Add(message.Id);
                    continue;
                }

                double? mapped = timeline.MapToMedia(message.At);
                if (mapped is null)
                    continue;

                double media = Math.Max(0, mapped.Value + offset);
                if (media > to)
                    continue;

                if (media < scanFrom)
                {
                    sawOlder = true;
                    continue;
                }

                collected.Add((message, media));
            }

            // One message beyond the limit proves that an older message exists.
            if (collected.Count > limit)
                break;
        }

        var messages = new List<ChatQueryMessage>(collected.Count);
        foreach ((StoredChatMessage message, double media) in collected)
            messages.Add(ToQueryMessage(message, media, tombstones, removedIds));
        messages.Sort(CompareMessages);

        bool hasMoreBefore = sawOlder || capped;
        if (messages.Count > limit)
        {
            messages.RemoveRange(0, messages.Count - limit);
            hasMoreBefore = true;
        }

        return (messages, hasMoreBefore);
    }

    private (IReadOnlyList<string> Files, bool Capped) TailBuckets(
        string directory, ChatTimeline timeline, double from, double to, int bucketSeconds)
    {
        HashSet<string> names = BucketNames(timeline, from, to, bucketSeconds);
        IReadOnlyList<string> allFiles = ChatLogLayout.BucketFiles(directory, logger);
        string[] files = allFiles
            .Where(path => names.Contains(Path.GetFileName(path)))
            .ToArray();

        // Newest first so the scan can stop as soon as the tail is full.
        return (
            files.TakeLast(MaxTailBuckets).Reverse().ToArray(),
            files.Length > MaxTailBuckets);
    }

    private static ChatQueryMessage ToQueryMessage(
        StoredChatMessage message,
        double media,
        ChatTombstones tombstones,
        IReadOnlySet<string> removedIds) =>
        new(
            message.Id,
            Math.Round(media, 3),
            message.At,
            message.UserId,
            message.Login,
            message.Display,
            message.Color,
            message.Badges,
            message.Text,
            message.Emotes,
            message.Bits,
            tombstones.RedactsReply(message, removedIds) ? null : message.Reply,
            message.Action);

    private static int CompareMessages(ChatQueryMessage left, ChatQueryMessage right)
    {
        int byTime = left.Mt.CompareTo(right.Mt);
        return byTime != 0
            ? byTime
            : string.CompareOrdinal(left.Id, right.Id);
    }


    private static IReadOnlyList<ChatQueryGap> BuildGaps(
        IReadOnlyList<StoredChatEvent> events,
        ChatTimeline timeline,
        double from,
        double to)
    {
        var gaps = new List<ChatQueryGap>();
        foreach (StoredChatEvent gap in events)
        {
            (double? gapFrom, double? gapTo, double seconds) = gap.Kind switch
            {
                ChatEventKinds.Gap or ChatEventKinds.Dropped when gap.From is { } start &&
                                                                  gap.To is { } end =>
                    (timeline.MapGapEndToMedia(start), timeline.MapGapEndToMedia(end),
                        (end - start) / 1000d),

                // A truncated log is open-ended: nothing after the cap was captured.
                ChatEventKinds.Truncated =>
                    (timeline.MapGapEndToMedia(gap.From ?? gap.At), to, double.NaN),

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
}
