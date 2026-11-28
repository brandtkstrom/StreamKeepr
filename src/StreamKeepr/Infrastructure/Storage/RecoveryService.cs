using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>The recovery result for one watched channel.</summary>
public sealed record RecoveredChannel(Resumable? Unfinished, Resumable? Finished);

/// <summary>
/// Reconstructs runtime state before any coordinator starts. It walks the session records
/// (ADR-0014) and reconciles only their folders: it repairs the finalize and reopen crash
/// windows (a Recording record with a final index completes; a Complete record without one is
/// re-stitched), removes temp/orphan files, finalizes sessions that cannot continue, and loads
/// the library. A folder without a record is ignored; a record whose folder or media is missing
/// or unreadable is logged, skipped, and kept, and its media is never deleted. Each run ends
/// with one Information summary whose counts account for every session record.
/// </summary>
public sealed class RecoveryService(
    SessionStore store,
    RecordingLibrary library,
    IOptions<StorageOptions> storage,
    TimeProvider clock,
    ILogger<RecoveryService> logger)
{
    public async Task<IReadOnlyDictionary<string, RecoveredChannel>> RecoverAsync(
        IReadOnlyList<ChannelEntry> channels, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channels);

        long startedAt = clock.GetTimestamp();
        string root = Path.GetFullPath(storage.Value.RecordingsRoot);
        var unfinished = new List<Resumable>();
        var finished = new List<Resumable>();
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        // Unfinished sessions with segment media but no complete segment line (a hard kill
        // mid-line). Finalize would delete them as empty, so they may resume but never finalize.
        var mediaWithoutSegments = new HashSet<string>(StringComparer.Ordinal);
        int sessionCount = 0;
        int completeCount = 0;
        int finalizedCount = 0;
        int deletedCount = 0;
        int skippedCount = 0;

        foreach (SessionRecord record in await store.LoadRecordsAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            sessionCount++;
            switch (await InspectSessionAsync(
                root, record, unfinished, finished, skipped, mediaWithoutSegments, ct))
            {
                case Inspection.Complete:
                    completeCount++;
                    break;
                case Inspection.Refinalized:
                    finalizedCount++;
                    break;
                case Inspection.Skipped:
                    skippedCount++;
                    break;
            }
        }

        var watched = channels.Select(channel => channel.Login).ToHashSet(StringComparer.Ordinal);
        var paused = channels
            .Where(channel => channel.Paused)
            .Select(channel => channel.Login)
            .ToHashSet(StringComparer.Ordinal);

        // Only the newest unfinished session of a watched, unpaused channel can still be live.
        var resumable = NewestPerChannel(
            unfinished.Where(candidate =>
                watched.Contains(candidate.Session.Channel)
                && !paused.Contains(candidate.Session.Channel)));

        var resumableIds = resumable.Values
            .Select(candidate => candidate.Session.Id)
            .ToHashSet(StringComparer.Ordinal);

        // Older sessions and sessions of removed channels will not continue.
        foreach (Resumable candidate in unfinished)
        {
            if (resumableIds.Contains(candidate.Session.Id))
                continue;

            if (mediaWithoutSegments.Contains(candidate.Session.Id))
            {
                logger.LogWarning(
                    "Skipping session {SessionId}: {Directory} has segment media but no complete segment line, so finalize would delete it.",
                    candidate.Session.Id,
                    candidate.Session.Directory);
                skipped.Add(candidate.Session.Id);
                skippedCount++;
                continue;
            }

            await store.FinalizeAsync(candidate.Session, ct);

            // FinalizeAsync deletes the record of a session that never produced media (then its
            // folder, which may outlive a failed delete), so the record decides.
            if (await store.TryLoadAsync(candidate.Session.Directory, ct) is not null)
            {
                finished.Add(candidate with { Complete = true });
                finalizedCount++;
            }
            else
            {
                deletedCount++;
            }
        }

        var newestFinished = NewestPerChannel(finished);

        await library.LoadAsync(root, store, ct);

        // Recovery skipped these (unsafe name, missing folder or media, unreadable or unrepairable
        // media);
        // never expose them even if LoadAsync could project their records.
        foreach (string skippedId in skipped)
            library.Remove(skippedId);

        // The counts partition SessionCount; DeletedCount is unfinished sessions without media.
        logger.LogInformation(
            "Recovered {SessionCount} sessions in {ElapsedMs:0} ms: {CompleteCount} already complete, {ResumableCount} resumable, {FinalizedCount} finalized, {DeletedCount} deleted, {SkippedCount} skipped.",
            sessionCount,
            clock.GetElapsedTime(startedAt).TotalMilliseconds,
            completeCount,
            resumable.Count,
            finalizedCount,
            deletedCount,
            skippedCount);

        return channels
            .Select(channel => channel.Login)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                channel => channel,
                channel => new RecoveredChannel(
                    resumable.GetValueOrDefault(channel),
                    newestFinished.GetValueOrDefault(channel)),
                StringComparer.Ordinal);
    }

    /// <summary>What recovery did with one session folder; feeds the startup summary.</summary>
    private enum Inspection
    {
        Skipped,
        Complete,
        Refinalized,
        Unfinished,
    }

    private async Task<Inspection> InspectSessionAsync(
        string root,
        SessionRecord record,
        List<Resumable> unfinished,
        List<Resumable> finished,
        HashSet<string> skipped,
        HashSet<string> mediaWithoutSegments,
        CancellationToken ct)
    {
        if (!store.TryResolveDirectory(root, record, out string directory))
        {
            skipped.Add(record.Id);
            return Inspection.Skipped;
        }

        // A recorded session whose folder is gone keeps its record and stays out of the
        // library; recovery never recreates or deletes anything for it.
        if (!Directory.Exists(directory))
        {
            logger.LogWarning(
                "Skipping session {SessionId}: its folder {Directory} is missing.", record.Id, directory);
            skipped.Add(record.Id);
            return Inspection.Skipped;
        }

        var session = new Session(
            record.Id, record.Channel, record.StreamId, record.StartedAt, directory);

        if (record.Status == SessionStatus.Complete)
        {
            if (await ReadFinalIndexAsync(directory, ct) is { } index)
            {
                if (!IndexedMediaIsPresent(directory, index))
                {
                    skipped.Add(record.Id);
                    return Inspection.Skipped;
                }

                finished.Add(new Resumable(session, LastPart(directory) + 1, Complete: true));
                return Inspection.Complete;
            }

            List<ScannedPart>? parts = await ScanPartsAsync(directory, ct);
            if (parts is null)
            {
                skipped.Add(record.Id);
                return Inspection.Skipped;
            }

            if (!parts.Any(part => part.HasSegments))
            {
                logger.LogWarning(
                    "Skipping complete session without an index or media parts at {Directory}.",
                    directory);
                skipped.Add(record.Id);
                return Inspection.Skipped;
            }

            // Reopen crash window, or a lost index: re-stitch from the parts and store the
            // recomputed aggregates.
            return await RefinalizeAsync(session, finished, skipped, ct);
        }

        CleanedSession? cleaned = await CleanupRecordingAsync(directory, ct);
        if (cleaned is null)
        {
            skipped.Add(record.Id);
            return Inspection.Skipped;
        }

        // Finalize crash window: the index was written but the record never said Complete, or a
        // reopen died between its record write and the index delete. Either way no recorder ran
        // after the index, so the session completes; re-finalizing recomputes its aggregates.
        if (await ReadFinalIndexAsync(directory, ct) is not null)
        {
            if (!cleaned.HasSegments)
            {
                logger.LogWarning(
                    "Skipping recording session with a final index but no media parts at {Directory}.",
                    directory);
                skipped.Add(record.Id);
                return Inspection.Skipped;
            }

            return await RefinalizeAsync(session, finished, skipped, ct);
        }

        if (!cleaned.HasSegments && cleaned.HasSegmentMedia)
            mediaWithoutSegments.Add(record.Id);

        unfinished.Add(new Resumable(session, cleaned.LastPart + 1));
        return Inspection.Unfinished;
    }

    /// <summary>
    /// Stitches a session that has media parts into its final index and marks the record
    /// Complete with recomputed aggregates, then adds it to finished. Unreadable media skips the
    /// session and keeps it.
    /// </summary>
    private async Task<Inspection> RefinalizeAsync(
        Session session, List<Resumable> finished, HashSet<string> skipped, CancellationToken ct)
    {
        try
        {
            await store.FinalizeAsync(session, ct);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            logger.LogWarning(
                ex,
                "Skipping session that could not be re-finalized at {Directory}.",
                session.Directory);
            skipped.Add(session.Id);
            return Inspection.Skipped;
        }

        finished.Add(new Resumable(session, LastPart(session.Directory) + 1, Complete: true));
        return Inspection.Refinalized;
    }

    /// <summary>
    /// Cleans a Recording session after a hard kill. Temp files are removed first; part
    /// playlists are then validated as a set, so one bad playlist skips the whole session
    /// before any media is deleted. Media an existing index names is also kept, so a session whose
    /// part playlists were lost after its index was written keeps its media. Returns null when the
    /// session must be skipped.
    /// </summary>
    private async Task<CleanedSession?> CleanupRecordingAsync(string directory, CancellationToken ct)
    {
        foreach (string temp in Directory.EnumerateFiles(directory, "*.tmp").ToArray())
            File.Delete(temp);

        List<ScannedPart>? parts = await ScanPartsAsync(directory, ct);
        if (parts is null)
            return null;

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        int lastPart = 0;

        foreach (ScannedPart scanned in parts)
        {
            lastPart = Math.Max(lastPart, scanned.Playlist.PartNumber);
            referenced.UnionWith(PlaylistStitcher.ReferencedMedia(scanned.Playlist.Content));
        }

        string indexPath = Path.Combine(directory, SessionStore.IndexFileName);
        if (File.Exists(indexPath))
        {
            try
            {
                string index = await PartPlaylistReader.ReadAllTextAsync(indexPath, ct);
                referenced.UnionWith(PlaylistStitcher.ReferencedMedia(index));
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                logger.LogWarning(
                    ex,
                    "Skipping session with an unreadable index at {Directory}; its media is kept.",
                    directory);
                return null;
            }
        }

        int removed = 0;
        IEnumerable<string> media = Directory.EnumerateFiles(directory, "*.m4s")
            .Concat(Directory.EnumerateFiles(directory, "init_*.mp4"));

        foreach (string file in media.ToArray())
        {
            if (referenced.Contains(Path.GetFileName(file)))
                continue;

            File.Delete(file);
            removed++;
        }

        if (removed > 0)
        {
            logger.LogWarning(
                "Removed {Count} unreferenced media files from {Directory}.",
                removed,
                directory);
        }

        return new CleanedSession(
            lastPart,
            HasSegments: parts.Any(part => part.HasSegments),
            HasSegmentMedia: Directory.EnumerateFiles(directory, "*.m4s").Any());
    }

    private async Task<List<ScannedPart>?> ScanPartsAsync(
        string directory, CancellationToken ct)
    {
        var parts = new List<ScannedPart>();

        foreach ((int part, string path) in store.PartPlaylistFiles(directory))
        {
            try
            {
                string content = await PartPlaylistReader.ReadAllTextAsync(path, ct);
                // The strict timeline parse validates the whole set before anything is
                // deleted; whether the part has segments uses the one stitcher definition.
                _ = PlaylistTimeline.Parse(content);
                parts.Add(new ScannedPart(
                    new PartPlaylist(part, content),
                    PlaylistStitcher.ParsePart(part, content).HasSegments));
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                logger.LogWarning(
                    ex,
                    "Skipping session with an unreadable part playlist at {Directory}.",
                    directory);
                return null;
            }
        }

        return parts;
    }

    /// <summary>The session's index when it exists, is readable and is final; otherwise null.</summary>
    private async Task<string?> ReadFinalIndexAsync(string directory, CancellationToken ct)
    {
        string path = Path.Combine(directory, SessionStore.IndexFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            string index = await PartPlaylistReader.ReadAllTextAsync(path, ct);
            return PlaylistStitcher.IsFinalIndex(index) ? index : null;
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            logger.LogWarning(
                ex, "Ignoring an unreadable session index at {Directory}.", directory);
            return null;
        }
    }

    /// <summary>
    /// False when none of the media the index names is on disk: the session is unplayable, so it
    /// is skipped (and kept) instead of listed. Some missing files only warn, since the rest
    /// still plays. One directory listing per session keeps startup cheap.
    /// </summary>
    private bool IndexedMediaIsPresent(string directory, string index)
    {
        IReadOnlySet<string> named = PlaylistStitcher.ReferencedMedia(index);
        var onDisk = Directory.EnumerateFiles(directory)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);
        int missing = named.Count(name => !onDisk.Contains(name));
        if (missing == 0)
            return true;

        if (missing < named.Count)
        {
            logger.LogWarning(
                "Session at {Directory} is missing {MissingCount} of {MediaCount} media files its index names; the rest still plays.",
                directory, missing, named.Count);
            return true;
        }

        logger.LogWarning(
            "Skipping complete session at {Directory}: none of the {MediaCount} media files its index names exist. The record is kept.",
            directory, named.Count);
        return false;
    }

    private int LastPart(string directory) =>
        store.PartPlaylistFiles(directory)
            .Select(part => part.Part)
            .DefaultIfEmpty(0)
            .Max();

    private static Dictionary<string, Resumable> NewestPerChannel(
        IEnumerable<Resumable> candidates) =>
        candidates
            .GroupBy(candidate => candidate.Session.Channel, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(candidate => candidate.Session.StartedAt)
                    .ThenByDescending(candidate => candidate.Session.Id, StringComparer.Ordinal)
                    .First(),
                StringComparer.Ordinal);

    private static bool IsReadFailure(Exception exception) =>
        exception is FormatException or IOException or UnauthorizedAccessException
            or InvalidDataException or NotSupportedException;

    private sealed record ScannedPart(PartPlaylist Playlist, bool HasSegments);

    /// <summary>A cleaned Recording session: its last part and what media is left.</summary>
    private sealed record CleanedSession(int LastPart, bool HasSegments, bool HasSegmentMedia);
}
