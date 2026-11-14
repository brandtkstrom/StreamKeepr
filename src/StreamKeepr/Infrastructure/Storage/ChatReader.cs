using System.Globalization;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Reads a bounded media-time window from the per-session chat log and applies tombstones,
/// deduplication, and the per-recording sync offset.
/// </summary>
public sealed class ChatReader(
    ChatLogStore store,
    IOptions<ChatOptions> options)
{
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
            return Empty(summary, fromSec, toSec, syncOffsetSec, "unavailable", false, "none");
        }

        StoredChatMeta? meta = store.ReadMeta(directory);
        bool hasChat = ChatLogStore.HasChat(directory);
        string source = meta?.Source ?? "none";
        int leadIn = meta?.LeadInSec ?? options.Value.LeadInSeconds;
        int bucketSeconds = meta?.BucketSeconds is > 0
            ? meta.BucketSeconds
            : options.Value.BucketMinutes * 60;

        ChatTimeline timeline = ChatTimeline.Build(directory, leadIn);
        if (!timeline.HasParts)
        {
            string state = summary.IsRecording ? "starting" : "unavailable";
            return Empty(summary, fromSec, toSec, syncOffsetSec, state, hasChat, source);
        }

        double duration = summary.DurationSeconds ?? double.MaxValue;
        double from = Math.Clamp(fromSec, 0, duration);
        double to = Math.Clamp(toSec, from, duration);
        double offset = Math.Clamp(syncOffsetSec, -30, 30);

        IReadOnlyList<StoredChatEvent> events = store.ReadEvents(directory);
        var deletes = events
            .Where(e => e.Kind == "delete" && e.TargetId is not null)
            .Select(e => e.TargetId!)
            .ToHashSet(StringComparer.Ordinal);
        long[] clearAll = events
            .Where(e => e.Kind == "clear_all")
            .Select(e => e.At)
            .ToArray();
        StoredChatEvent[] clearUser = events
            .Where(e => e.Kind == "clear_user")
            .ToArray();

        var messages = new List<ChatQueryMessage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool hasMoreBefore = false;
        bool hasMoreAfter = false;

        foreach (string bucketPath in this.BucketFiles(directory, timeline, from, to, bucketSeconds))
        {
            foreach (StoredChatMessage message in store.ReadMessages(bucketPath))
            {
                if (!string.IsNullOrEmpty(message.Id) && !seen.Add(message.Id))
                    continue;

                if (IsCleared(message, deletes, clearAll, clearUser))
                    continue;

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
                    message.Reply,
                    message.Action));
            }
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
        foreach (StoredChatEvent gap in events.Where(
                     e => (e.Kind is "gap" or "dropped") &&
                          e.From is not null && e.To is not null))
        {
            double? gapFrom = timeline.MapToMedia(gap.From!.Value);
            double? gapTo = timeline.MapToMedia(gap.To!.Value);
            if (gapFrom is null || gapTo is null || gapTo <= gapFrom)
                continue;

            double start = Math.Max(from, gapFrom.Value);
            double end = Math.Min(to, gapTo.Value);
            if (end > start)
                gaps.Add(new ChatQueryGap(Math.Round(start, 3), Math.Round(end, 3)));
        }

        string resultState = summary.IsRecording ? "recording" : "complete";
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

    private static bool IsCleared(
        StoredChatMessage message,
        HashSet<string> deletes,
        long[] clearAll,
        StoredChatEvent[] clearUser)
    {
        if (deletes.Contains(message.Id))
            return true;

        foreach (long at in clearAll)
        {
            if (message.At <= at)
                return true;
        }

        foreach (StoredChatEvent clear in clearUser)
        {
            if (message.At > clear.At)
                continue;

            bool uidMatches = clear.TargetUid is not null &&
                              string.Equals(clear.TargetUid, message.UserId, StringComparison.Ordinal);
            bool loginMatches = clear.TargetLogin is not null &&
                                string.Equals(clear.TargetLogin, message.Login,
                                    StringComparison.OrdinalIgnoreCase);
            if (uidMatches || loginMatches)
                return true;
        }

        return false;
    }

    private IReadOnlyList<string> BucketFiles(
        string directory, ChatTimeline timeline, double from, double to, int bucketSeconds)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach ((DateTimeOffset rangeFrom, DateTimeOffset rangeTo) in timeline.WallRanges(from, to))
        {
            DateTimeOffset start = Floor(rangeFrom, bucketSeconds).AddSeconds(-bucketSeconds);
            DateTimeOffset end = Floor(rangeTo, bucketSeconds).AddSeconds(bucketSeconds);
            for (DateTimeOffset bucket = start; bucket <= end;
                 bucket = bucket.AddSeconds(bucketSeconds))
            {
                names.Add(bucket.UtcDateTime.ToString(
                    "yyyyMMdd'T'HHmm'Z'", CultureInfo.InvariantCulture) + ".jsonl");
            }
        }

        string chatDirectory = ChatLogStore.ChatDirectory(directory);
        if (!Directory.Exists(chatDirectory))
            return [];

        try
        {
            return Directory.EnumerateFiles(chatDirectory, "*.jsonl")
                .Where(path => names.Contains(Path.GetFileName(path)))
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

    private static DateTimeOffset Floor(DateTimeOffset at, int bucketSeconds)
    {
        long seconds = at.ToUnixTimeSeconds();
        long floor = seconds / bucketSeconds * bucketSeconds;
        return DateTimeOffset.FromUnixTimeSeconds(floor);
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
            Math.Clamp(syncOffsetSec, -30, 30),
            [],
            [],
            false,
            false);
}
