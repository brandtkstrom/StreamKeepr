using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Reads a bounded media-time window from the per-session chat log and applies tombstones,
/// deduplication, and the per-recording sync offset.
/// </summary>
public sealed class ChatReader(
    ChatLogStore store,
    IOptions<ChatOptions> options,
    ILogger<ChatReader> logger)
{
    /// <summary>The largest per-recording sync nudge, in either direction.</summary>
    public const double MaxSyncOffsetSeconds = 30;

    public ChatQueryResult Query(
        RecordingSummary summary,
        double fromSec,
        double toSec,
        int limit,
        double syncOffsetSec)
    {
        ArgumentNullException.ThrowIfNull(summary);

        string directory;
        try
        {
            directory = store.SessionDirectory(summary);
        }
        catch (ArgumentException)
        {
            return Empty(summary, fromSec, toSec, syncOffsetSec, ChatStates.Unavailable, false, ChatSources.None);
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
            return Empty(summary, fromSec, toSec, syncOffsetSec, state, hasChat, source);
        }

        double duration = summary.DurationSeconds ?? double.MaxValue;
        double from = Math.Clamp(fromSec, 0, duration);
        double to = Math.Clamp(toSec, from, duration);
        double offset = Math.Clamp(syncOffsetSec, -MaxSyncOffsetSeconds, MaxSyncOffsetSeconds);

        IReadOnlyList<StoredChatEvent> events = store.ReadEvents(directory);
        ChatTombstones tombstones = ChatTombstones.From(events);

        var candidates = new List<StoredChatMessage>();
        var removedIds = new HashSet<string>(StringComparer.Ordinal);
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

            messages.Add(new ChatQueryMessage(
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
                message.Action));
        }

        messages.Sort((left, right) =>
        {
            int byTime = left.Mt.CompareTo(right.Mt);
            return byTime != 0
                ? byTime
                : string.CompareOrdinal(left.Id, right.Id);
        });

        if (messages.Count > limit)
        {
            hasMoreAfter = true;
            messages.RemoveRange(limit, messages.Count - limit);
        }

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

        return ChatLogLayout.BucketFiles(directory, logger)
            .Where(path => names.Contains(Path.GetFileName(path)))
            .ToArray();
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
