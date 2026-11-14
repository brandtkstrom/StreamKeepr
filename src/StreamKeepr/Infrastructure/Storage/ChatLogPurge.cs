using System.Text.Json;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// The opt-in finalize purge (ADR 0009): removes deleted and cleared messages from a closed
/// chat log, redacts replies that quote them, and replaces the tombstones with one
/// <c>purged</c> marker. It applies the same <see cref="ChatTombstones"/> rule as query time.
/// </summary>
internal static class ChatLogPurge
{
    /// <summary>
    /// Purges a closed session's chat log. Every file is read strictly before any rewrite, so
    /// an unreadable log is left as it is; a failure is logged, never thrown.
    /// </summary>
    /// <param name="fallbackMeta">Builds the metadata to start from when <c>meta.json</c> is unreadable.</param>
    public static void PurgeDeleted(
        string sessionDirectory,
        string sessionId,
        Func<StoredChatMeta> fallbackMeta,
        TimeProvider clock,
        ILogger logger)
    {
        if (!Directory.Exists(ChatLogLayout.ChatDirectory(sessionDirectory)))
            return;

        string eventsPath = ChatLogLayout.EventsPath(sessionDirectory);
        string metaPath = ChatLogLayout.MetaPath(sessionDirectory);
        try
        {
            // Strict reads, all before any rewrite: an unreadable log is left as it is.
            List<StoredChatEvent> events = File.Exists(eventsPath)
                ? ChatLogJson.ReadLinesStrict<StoredChatEvent>(eventsPath)
                : [];
            ChatTombstones tombstones = ChatTombstones.From(events);
            if (tombstones.IsEmpty)
                return;

            // Pass 1 finds every removed message, so a reply in any bucket can be redacted
            // regardless of the order buckets are rewritten in.
            var buckets = new List<(string Path, List<StoredChatMessage> Kept)>();
            var removedIds = new HashSet<string>(StringComparer.Ordinal);
            int removed = 0;
            foreach (string bucketPath in ChatLogLayout.BucketFiles(sessionDirectory, logger))
            {
                var kept = new List<StoredChatMessage>();
                foreach (StoredChatMessage message in ChatLogJson.ReadLinesStrict<StoredChatMessage>(bucketPath))
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

                ChatLogJson.WriteAllTextAtomically(bucketPath, ChatLogJson.ToJsonLines(kept));
                keptTotal += kept.Count;
                lastAt = Math.Max(lastAt, kept[^1].At);
            }

            List<StoredChatEvent> keptEvents = events
                .Where(e => !ChatEventKinds.IsTombstone(e.Kind))
                .Append(StoredChatEvent.Create(ChatEventKinds.Purged, clock.GetUtcNow().ToUnixTimeMilliseconds()) with
                {
                    Count = removed,
                    Reason = $"{removed} messages, {redacted} replies",
                })
                .ToList();
            ChatLogJson.WriteAllTextAtomically(eventsPath, ChatLogJson.ToJsonLines(keptEvents));

            StoredChatMeta? previous = ChatLogJson.TryRead<StoredChatMeta>(metaPath, logger);
            StoredChatMeta meta = (previous ?? fallbackMeta()) with
            {
                MessageCount = keptTotal,
                LastMessageAt = lastAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(lastAt) : null,
                PurgedCount = (previous?.PurgedCount ?? 0) + removed,
                PurgedAt = clock.GetUtcNow(),
                PurgedDeletedText = true,
            };
            ChatLogJson.WriteAllTextAtomically(metaPath, ChatLogJson.Serialize(meta));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Chat purge failed for {Session}; rewritten buckets stay purged and the query path still hides deleted text.",
                sessionId);
        }
    }
}
