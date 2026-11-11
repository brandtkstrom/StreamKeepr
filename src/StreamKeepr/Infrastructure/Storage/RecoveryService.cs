using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>The recovery result for one watched channel.</summary>
public sealed record RecoveredChannel(Resumable? Unfinished, Resumable? Finished);

/// <summary>
/// Reconstructs runtime state from the recordings volume before any coordinator starts. It
/// re-finalizes interrupted sessions, removes temp/orphan files, finalizes sessions that cannot
/// continue, and loads the library. Corrupt folders are logged and skipped, never deleted.
/// </summary>
public sealed class RecoveryService(
    SessionStore store,
    RecordingLibrary library,
    IOptions<StorageOptions> storage,
    ILogger<RecoveryService> logger)
{
    private const string MapPrefix = "#EXT-X-MAP:URI=\"";

    public async Task<IReadOnlyDictionary<string, RecoveredChannel>> RecoverAsync(
        IReadOnlyList<ChannelEntry> channels, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channels);

        string root = Path.GetFullPath(storage.Value.RecordingsRoot);
        var unfinished = new List<Resumable>();
        var finished = new List<Resumable>();
        var skipped = new HashSet<string>(StringComparer.Ordinal);

        if (Directory.Exists(root))
        {
            foreach (string channelDirectory in Directory.EnumerateDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                if (IsHidden(channelDirectory))
                    continue;

                foreach (string sessionDirectory in Directory.EnumerateDirectories(channelDirectory))
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsHidden(sessionDirectory))
                        continue;

                    await InspectSessionAsync(sessionDirectory, unfinished, finished, skipped, ct);
                }
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

            await store.FinalizeAsync(candidate.Session, ct);

            // FinalizeAsync deletes a session that never produced media.
            if (Directory.Exists(candidate.Session.Directory))
                finished.Add(candidate with { Complete = true });
        }

        var newestFinished = NewestPerChannel(finished);

        await library.LoadAsync(root, store, ct);

        // A Complete session that could not be repaired may still have been projected by
        // LoadAsync from its parts; never expose an unplayable entry.
        foreach (string skippedId in skipped)
            library.Remove(skippedId);

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

    private async Task InspectSessionAsync(
        string directory,
        List<Resumable> unfinished,
        List<Resumable> finished,
        HashSet<string> skipped,
        CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(directory, SessionStore.ManifestFileName)))
        {
            logger.LogWarning("Skipping session without a manifest at {Directory}.", directory);
            return;
        }

        // TryLoadAsync logs and returns null for an unreadable manifest.
        if (await store.TryLoadAsync(directory, ct) is not { } manifest)
            return;

        var session = new Session(
            manifest.Id, manifest.Channel, manifest.StreamId, manifest.StartedAt, directory);

        if (manifest.Status == SessionStatus.Complete)
        {
            if (await IndexIsValidAsync(directory, ct))
            {
                finished.Add(new Resumable(session, LastPart(directory) + 1, Complete: true));
                return;
            }

            List<ScannedPart>? parts = await ScanPartsAsync(directory, ct);
            if (parts is null)
            {
                skipped.Add(manifest.Id);
                return;
            }

            if (parts.Count == 0 || parts.All(part => part.SegmentCount == 0))
            {
                logger.LogWarning(
                    "Skipping complete session without an index or media parts at {Directory}.",
                    directory);
                skipped.Add(manifest.Id);
                return;
            }

            try
            {
                await store.FinalizeAsync(session, ct);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                logger.LogWarning(
                    ex,
                    "Skipping complete session that could not be re-finalized at {Directory}.",
                    directory);
                skipped.Add(manifest.Id);
                return;
            }

            finished.Add(new Resumable(session, LastPart(directory) + 1, Complete: true));
            return;
        }

        int? lastPart = await CleanupRecordingAsync(directory, ct);
        if (lastPart is null)
        {
            skipped.Add(manifest.Id);
            return;
        }

        unfinished.Add(new Resumable(session, lastPart.Value + 1));
    }

    /// <summary>
    /// Cleans a Recording session after a hard kill. Temp files are removed first; part
    /// playlists are then validated as a set, so one bad playlist skips the whole session
    /// before any media is deleted.
    /// </summary>
    private async Task<int?> CleanupRecordingAsync(string directory, CancellationToken ct)
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

            foreach (string raw in scanned.Playlist.Content.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;

                if (line.StartsWith(MapPrefix, StringComparison.Ordinal))
                {
                    if (line.EndsWith('"') && line.Length > MapPrefix.Length)
                        referenced.Add(line[MapPrefix.Length..^1]);
                }
                else if (line[0] != '#')
                {
                    referenced.Add(line);
                }
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

        return lastPart;
    }

    private async Task<List<ScannedPart>?> ScanPartsAsync(
        string directory, CancellationToken ct)
    {
        var parts = new List<ScannedPart>();

        foreach ((int part, string path) in store.PartPlaylistFiles(directory))
        {
            try
            {
                string content = await File.ReadAllTextAsync(path, ct);
                int segments = PlaylistStitcher.SegmentCount(content);
                parts.Add(new ScannedPart(new PartPlaylist(part, content), segments));
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

    private async Task<bool> IndexIsValidAsync(string directory, CancellationToken ct)
    {
        string path = Path.Combine(directory, SessionStore.IndexFileName);
        if (!File.Exists(path))
            return false;

        try
        {
            string index = await File.ReadAllTextAsync(path, ct);
            return PlaylistStitcher.SegmentCount(index) > 0
                && index.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            logger.LogWarning(
                ex, "Ignoring an unreadable session index at {Directory}.", directory);
            return false;
        }
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

    private static bool IsHidden(string path) =>
        Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal);

    private static bool IsReadFailure(Exception exception) =>
        exception is FormatException or IOException or UnauthorizedAccessException
            or InvalidDataException or NotSupportedException;

    private sealed record ScannedPart(PartPlaylist Playlist, int SegmentCount);
}
