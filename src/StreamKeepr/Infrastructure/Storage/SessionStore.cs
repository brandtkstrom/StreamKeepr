using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Records;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Owns the session records, the per-session locks, atomic index writes, and playlist stitching.
/// The recorder writes the part files; this store writes the session's record-store rows
/// (ADR-0014) and <c>index.m3u8</c>, and projects both into the <see cref="RecordingLibrary"/>.
/// </summary>
/// <remarks>
/// Every mutation takes <see cref="LockFor"/> for its session folder. Public methods never call
/// another locking method while holding a lock; the <c>*Core</c> helpers assume the caller holds it.
/// The record is the commit point: a lifecycle change counts once its row is written, and a
/// failed row write throws and leaves the previous record in place. While a session records,
/// its counts, size, and chapters are derived from the part playlists and never stored.
/// </remarks>
public sealed class SessionStore(
    IOptions<RecorderOptions> recorder,
    IDbContextFactory<RecordStoreDbContext> contexts,
    RecordingLibrary library,
    TimeProvider clock,
    ILogger<SessionStore> logger,
    Func<TimeSpan, CancellationToken, Task> retryDelay)
{
    public const string IndexFileName = "index.m3u8";
    public const string ThumbnailFileName = "thumb.jpg";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly SessionRecords _records = new(contexts, logger);

    internal int MinTargetDuration => 2 * recorder.Value.SegmentSeconds;

    /// <summary>The one lock for a session folder. Keep the entry after finalize; delete it on delete.</summary>
    public SemaphoreSlim LockFor(string sessionDir)
    {
        string directory = Normalize(sessionDir);
        return _locks.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>Removes the lock entry once its session folder has been deleted.</summary>
    public void ForgetLock(string sessionDir) =>
        _locks.TryRemove(Normalize(sessionDir), out _);

    /// <summary>
    /// Creates a session, or reopens the existing one. Never overwrites an existing record or
    /// its metadata history. A failed record write throws, so the recorder does not start.
    /// </summary>
    public async Task OpenAsync(Session session, LiveStream live, CancellationToken ct)
    {
        string directory = Normalize(session.Directory);
        var gate = LockFor(directory);
        await gate.WaitAsync(ct);
        try
        {
            if (await _records.FindAsync(session.Id, ct) is { } existing)
            {
                await ReopenCoreAsync(directory, existing, ct);
                return;
            }

            // The folder comes first: a failed insert leaves an empty folder that nothing
            // reads, and the next open inserts the record again.
            Directory.CreateDirectory(directory);
            var opened = new SessionRecord(
                session.Id,
                session.Channel,
                session.StreamId,
                session.StartedAt,
                SessionStatus.Recording,
                ImmutableList.Create(new MetadataChange(clock.GetUtcNow(), live.Title, live.Game)));
            await _records.CreateAsync(session, opened.Metadata, endedAt: null, ct);

            await UpsertLibraryAsync(directory, opened, ct);
            logger.LogInformation("Opened session {SessionId} in {Directory}.", session.Id, directory);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Records a session with a known metadata history without touching its folder, for the Mock
    /// seeder, which writes the part files itself and then finalizes Complete sessions. An
    /// <paramref name="endedAt"/> is kept by that finalize.
    /// </summary>
    public async Task SeedAsync(
        Session session,
        IReadOnlyList<MetadataChange> metadata,
        DateTimeOffset? endedAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(metadata);

        string directory = Normalize(session.Directory);
        var gate = LockFor(directory);
        await gate.WaitAsync(ct);
        try
        {
            await _records.CreateAsync(session, metadata, endedAt, ct);
            await UpsertLibraryAsync(
                directory,
                new SessionRecord(
                    session.Id,
                    session.Channel,
                    session.StreamId,
                    session.StartedAt,
                    SessionStatus.Recording,
                    metadata.ToImmutableList(),
                    endedAt),
                ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Returns a finalized session to Recording without touching its part files.</summary>
    public async Task ReopenAsync(Session session, CancellationToken ct)
    {
        string directory = Normalize(session.Directory);
        var gate = LockFor(directory);
        await gate.WaitAsync(ct);
        try
        {
            SessionRecord record = await RequireRecordAsync(session.Id, ct);
            await ReopenCoreAsync(directory, record, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Appends a title/game change. A repeated observation with the same title and game writes
    /// nothing. The latest entry is the session's current title and game. A failed write throws
    /// and changes nothing; the caller logs it and keeps recording.
    /// </summary>
    public async Task AppendMetadataAsync(Session session, LiveStream live, CancellationToken ct)
    {
        string directory = Normalize(session.Directory);
        var gate = LockFor(directory);
        await gate.WaitAsync(ct);
        try
        {
            SessionRecord record = await RequireRecordAsync(session.Id, ct);

            var latest = record.Metadata[^1];
            if (latest.Title == live.Title && latest.Game == live.Game)
                return;

            var change = new MetadataChange(clock.GetUtcNow(), live.Title, live.Game);
            await _records.AppendMetadataAsync(session.Id, change, ct);
            await UpsertLibraryAsync(
                directory, record with { Metadata = record.Metadata.Add(change) }, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Appends one viewer sample to the session's <c>viewers.jsonl</c> under the session lock.
    /// Never creates the session folder, so a sample cannot resurrect a deleted session. A
    /// previous partial line is terminated first, so it costs only itself.
    /// </summary>
    public async Task AppendViewerSampleAsync(
        Session session, DateTimeOffset at, int count, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        string directory = Normalize(session.Directory);
        string line = ViewerSamples.FormatLine(ViewerSamples.OffsetSeconds(session.StartedAt, at), count);
        var gate = LockFor(directory);
        await gate.WaitAsync(ct);
        try
        {
            // OpenOrCreate creates the file but never its folder: a removed session throws
            // DirectoryNotFoundException instead of being recreated.
            await using var stream = new FileStream(
                Path.Combine(directory, ViewerSamples.FileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read);

            bool needsSeparator = false;
            if (stream.Length > 0)
            {
                stream.Seek(-1, SeekOrigin.End);
                needsSeparator = stream.ReadByte() != '\n';
            }

            stream.Seek(0, SeekOrigin.End);
            byte[] bytes = Encoding.UTF8.GetBytes(
                (needsSeparator ? "\n" : "") + line + "\n");
            await stream.WriteAsync(bytes, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Stitches every non-empty part into <c>index.m3u8</c> with one ENDLIST and marks the record
    /// Complete with its final aggregates. A session with no media is deleted. If the record
    /// write fails, the session stays Recording and startup recovery finalizes it.
    /// </summary>
    public async Task FinalizeAsync(Session session, CancellationToken ct)
    {
        string directory = Normalize(session.Directory);
        var gate = LockFor(directory);
        await gate.WaitAsync(ct);
        try
        {
            await FinalizeCoreAsync(directory, session.Id, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Moves a finished thumbnail image into the session as <c>thumb.jpg</c>, records it, and
    /// refreshes the library. The thumbnail is written once and never replaced, because
    /// <c>/media</c> serves it as immutable. <paramref name="imagePath"/> must be on the
    /// recordings volume so the move is a rename. The image is consumed either way: returns
    /// false (and deletes it) when the session no longer exists or already has a thumbnail.
    /// </summary>
    public async Task<bool> SaveThumbnailAsync(string sessionDir, string imagePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        string directory = Normalize(sessionDir);
        string target = Path.Combine(directory, ThumbnailFileName);
        var gate = LockFor(directory);
        await gate.WaitAsync(ct);
        try
        {
            // A delete holds this lock while it removes the record, so the check is race-free.
            SessionRecord? record = await _records.FindAsync(SessionIdOf(directory), ct);
            if (record is null || File.Exists(target))
            {
                File.Delete(imagePath);

                // A record write that failed after an earlier move left the file unrecorded;
                // the existing image is the thumbnail, so record it now.
                if (record is { HasThumbnail: false })
                    await RecordThumbnailAsync(directory, record, ct);

                return false;
            }

            File.Move(imagePath, target, overwrite: false);
            await RecordThumbnailAsync(directory, record, ct);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reads the session's record and derives its library summary. Returns null when the session
    /// has no record. A Recording session's counts, size, and chapters come from its part
    /// playlists; a Complete session's come from its record, positioned on its index.
    /// </summary>
    public async Task<RecordingSummary?> SummarizeAsync(string sessionDir, CancellationToken ct)
    {
        string directory = Normalize(sessionDir);
        SessionRecord? record = await TryLoadAsync(directory, ct);
        return record is null ? null : await ProjectAsync(directory, record, ct);
    }

    /// <summary>
    /// Derives the summary of every recorded session under <paramref name="recordingsRoot"/> for
    /// a library load. A session whose folder name is unsafe, whose folder is missing, or whose
    /// media cannot be read is logged and left out; its record is kept.
    /// </summary>
    public async Task<IReadOnlyList<RecordingSummary>> SummarizeAllAsync(
        string recordingsRoot, CancellationToken ct)
    {
        IReadOnlyList<SessionRecord> records = await _records.AllAsync(ct);
        var summaries = new List<RecordingSummary>(records.Count);
        foreach (SessionRecord record in records)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryResolveDirectory(recordingsRoot, record, out string directory))
                continue;

            // A Recording projection reads no file when its folder is gone, so check first:
            // a session whose folder is missing is never listed.
            if (!Directory.Exists(directory))
            {
                logger.LogWarning(
                    "Not listing session {SessionId}: its folder {Directory} is missing.",
                    record.Id,
                    directory);
                continue;
            }

            try
            {
                summaries.Add(await ProjectAsync(directory, record, ct));
            }
            catch (Exception ex) when (IsProjectionFailure(ex))
            {
                logger.LogWarning(ex, "Skipping unreadable session in {Directory}.", directory);
            }
        }

        return summaries;
    }

    /// <summary>Every session record, for startup recovery. Order is unspecified.</summary>
    public Task<IReadOnlyList<SessionRecord>> LoadRecordsAsync(CancellationToken ct) =>
        _records.AllAsync(ct);

    /// <summary>
    /// The session folder for a record, refusing a record whose channel or id would leave the
    /// recordings root.
    /// </summary>
    public bool TryResolveDirectory(string recordingsRoot, SessionRecord record, out string directory)
    {
        if (Session.IsValidId(record.Id)
            && RecordingsPath.TryResolve(recordingsRoot, [record.Channel, record.Id], out directory))
        {
            directory = Normalize(directory);
            return true;
        }

        logger.LogWarning(
            "Skipping session {SessionId}: its channel or id is not a safe folder name.", record.Id);
        directory = "";
        return false;
    }

    /// <summary>
    /// Reads the record of the session in <paramref name="sessionDir"/> (the folder is named for
    /// the session id) without taking the lock. Returns null when the session has no record; a
    /// record-store failure throws.
    /// </summary>
    public Task<SessionRecord?> TryLoadAsync(string sessionDir, CancellationToken ct) =>
        _records.FindAsync(SessionIdOf(Normalize(sessionDir)), ct);

    /// <summary>
    /// Deletes a session's record and its dependent rows. The caller holds <see cref="LockFor"/>
    /// and removes the library entry and folder itself.
    /// </summary>
    public Task DeleteRecordAsync(string sessionId, CancellationToken ct) =>
        _records.DeleteAsync(sessionId, ct);

    private async Task ReopenCoreAsync(string directory, SessionRecord record, CancellationToken ct)
    {
        // The record changes first: a failed write leaves the session Complete with its index,
        // and a failed index delete afterwards leaves a Recording session that the next
        // finalize re-stitches.
        await _records.MarkRecordingAsync(record.Id, ct);

        string indexPath = Path.Combine(directory, IndexFileName);
        if (File.Exists(indexPath))
            File.Delete(indexPath);

        var reopened = record with
        {
            Status = SessionStatus.Recording,
            EndedAt = null,
            DurationSeconds = null,
            SizeBytes = null,
            PartCount = null,
            SegmentCount = null,
        };
        await UpsertLibraryAsync(directory, reopened, ct);
        logger.LogInformation("Reopened session {SessionId} in {Directory}.", reopened.Id, directory);
    }

    private async Task FinalizeCoreAsync(string directory, string sessionId, CancellationToken ct)
    {
        // Callers hold the session lock, so ElapsedMs times the finalize work, not the wait.
        long startedAt = clock.GetTimestamp();
        SessionRecord record = await RequireRecordAsync(sessionId, ct);
        var parts = await ReadPartsAsync(directory, ct);

        if (parts.Count == 0)
        {
            // The record goes first; a folder left behind by a failed delete is never read.
            await _records.DeleteAsync(record.Id, ct);
            library.Remove(record.Id);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);

            logger.LogInformation("Deleted empty session {SessionId}.", record.Id);
            return;
        }

        string index = PlaylistStitcher.Stitch(parts, MinTargetDuration, final: true);
        await WriteAtomicAsync(Path.Combine(directory, IndexFileName), index, ct);

        PlaylistTimeline timeline = PlaylistTimeline.Parse(index);
        var complete = record with
        {
            Status = SessionStatus.Complete,
            EndedAt = record.EndedAt ?? clock.GetUtcNow(),
            DurationSeconds = timeline.Duration,
            SizeBytes = MediaSize(directory),
            PartCount = parts.Count,
            SegmentCount = timeline.SegmentCount,
        };
        await _records.MarkCompleteAsync(complete, ct);

        // Project from the inputs already in memory instead of re-reading every part and the
        // index while the session lock is still held.
        TryUpsertLibrary(
            directory,
            () => ToSummary(
                directory, complete, parts.Count, timeline.SegmentCount, complete.SizeBytes,
                timeline.Chapters(complete.Metadata)));

        logger.LogInformation(
            "Finalized session {SessionId} for {Channel} in {ElapsedMs:0} ms: {PartCount} parts, {SizeBytes} bytes in {Directory}.",
            complete.Id,
            complete.Channel,
            clock.GetElapsedTime(startedAt).TotalMilliseconds,
            parts.Count,
            complete.SizeBytes,
            directory);
    }

    private async Task<SessionRecord> RequireRecordAsync(string sessionId, CancellationToken ct) =>
        await _records.FindAsync(sessionId, ct)
            ?? throw new InvalidDataException($"Session {sessionId} has no record.");

    private async Task RecordThumbnailAsync(string directory, SessionRecord record, CancellationToken ct)
    {
        await _records.MarkThumbnailAsync(record.Id, ct);
        await UpsertLibraryAsync(directory, record with { HasThumbnail = true }, ct);
    }

    /// <summary>
    /// Derives a summary from a record. A Recording session is measured from its part
    /// playlists; a Complete session uses its stored aggregates, requires its index, and reads
    /// the index only to position chapters, so a session without title changes reads no file.
    /// </summary>
    private async Task<RecordingSummary> ProjectAsync(
        string directory, SessionRecord record, CancellationToken ct)
    {
        if (record.Status == SessionStatus.Recording)
        {
            var parts = await ReadPartsAsync(directory, ct);
            var live = PlaylistTimeline.Parse(PlaylistStitcher.Stitch(parts, MinTargetDuration, final: false));
            return ToSummary(
                directory, record, parts.Count, live.SegmentCount, MediaSize(directory),
                live.Chapters(record.Metadata));
        }

        // Never list a Complete session that cannot be played; recovery re-stitches a
        // missing index before the library loads.
        string indexPath = Path.Combine(directory, IndexFileName);
        if (!File.Exists(indexPath))
            throw MissingIndex(record);

        IReadOnlyList<Chapter> chapters = [];
        if (record.Metadata.Count > 1)
        {
            string index = await TryReadFileAsync(indexPath, ct) ?? throw MissingIndex(record);
            chapters = PlaylistTimeline.Parse(index).Chapters(record.Metadata);
        }

        return ToSummary(
            directory, record, record.PartCount ?? 0, record.SegmentCount ?? 0, record.SizeBytes, chapters);
    }

    private static InvalidDataException MissingIndex(SessionRecord record) =>
        new($"Complete session {record.Id} has no index.");

    private RecordingSummary ToSummary(
        string directory,
        SessionRecord record,
        int partCount,
        int segmentCount,
        long? sizeBytes,
        IReadOnlyList<Chapter> chapters)
    {
        bool complete = record.Status == SessionStatus.Complete;
        MetadataChange latest = record.Metadata[^1];
        return new RecordingSummary(
            record.Id,
            record.Channel,
            latest.Title,
            latest.Game,
            record.StartedAt,
            complete ? record.EndedAt : null,
            IsRecording: !complete,
            complete ? record.DurationSeconds : null,
            sizeBytes,
            partCount,
            segmentCount,
            record.HasThumbnail,
            chapters,
            // Interim until #317 moves chat into the record store: chat is still captured to the
            // chat/ sidecar, so a session without a chat row still has chat when that exists.
            // #317 removes the sidecar check and keeps only the row.
            HasChat: record.HasChatSession || ChatLogLayout.HasChat(directory, logger));
    }

    private async Task UpsertLibraryAsync(string directory, SessionRecord record, CancellationToken ct)
    {
        try
        {
            library.Upsert(await ProjectAsync(directory, record, ct));
        }
        catch (Exception ex) when (IsProjectionFailure(ex))
        {
            LogProjectionFailure(directory, ex);
        }
    }

    private void TryUpsertLibrary(string directory, Func<RecordingSummary> project)
    {
        try
        {
            library.Upsert(project());
        }
        catch (Exception ex) when (IsProjectionFailure(ex))
        {
            LogProjectionFailure(directory, ex);
        }
    }

    private void LogProjectionFailure(string directory, Exception exception) =>
        // The record mutation already succeeded. A projection failure must not turn a
        // recording command into a failure; recovery or the next update will rebuild the
        // summary once the media is readable.
        logger.LogWarning(
            exception, "Could not update the recording library for {Directory}.", directory);

    private static bool IsProjectionFailure(Exception ex) =>
        ex is FormatException or IOException or InvalidDataException
            or UnauthorizedAccessException or NotSupportedException;

    /// <summary>A session folder is named for its session id (<see cref="Session.For"/>).</summary>
    private static string SessionIdOf(string directory) => Path.GetFileName(directory);

    private async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        string temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, ct);

        // Windows refuses the replace while another handle without delete sharing still holds
        // the destination (a scanner, an external reader). Retry the sharing violation only.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 5 && IsSharingViolation(ex) && File.Exists(temp))
            {
                logger.LogDebug(
                    "Retrying atomic replace of {Path} after a sharing violation (attempt {Attempt}).",
                    path,
                    attempt);
                await retryDelay(TimeSpan.FromMilliseconds(10 * attempt), ct);
            }
        }
    }

    /// <summary>
    /// Fresh length and last-write metadata for every valid part playlist, in ascending part
    /// order. Cheap enough for every live refresh; reads no content and takes no lock.
    /// </summary>
    internal IReadOnlyList<PartFileObservation> ObservePartFiles(string directory)
    {
        List<(int Part, string Path)> files = PartPlaylistFiles(directory);
        var observations = new PartFileObservation[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            var info = new FileInfo(files[i].Path);
            observations[i] = new PartFileObservation(
                files[i].Part, files[i].Path, info.Length, info.LastWriteTimeUtc.Ticks);
        }

        return observations;
    }

    private static bool IsSharingViolation(Exception exception) =>
        exception is UnauthorizedAccessException
        || (exception is IOException io
            && (io.HResult & 0xFFFF) is 32 or 33); // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION

    private async Task<List<PartPlaylist>> ReadPartsAsync(string directory, CancellationToken ct)
    {
        var parts = new List<PartPlaylist>();

        foreach (var (number, path) in PartPlaylistFiles(directory))
        {
            string content = await PartPlaylistReader.ReadAllTextAsync(path, ct);
            if (PlaylistStitcher.ParsePart(number, content).HasSegments)
                parts.Add(new PartPlaylist(number, content));
        }

        return parts;
    }

    /// <summary>
    /// Valid part playlists in ascending part order. Recovery uses this before it deletes
    /// temp files or orphaned media; callers outside the store must not mutate the files.
    /// </summary>
    internal List<(int Part, string Path)> PartPlaylistFiles(string directory)
    {
        var parts = new List<(int Part, string Path)>();
        if (!Directory.Exists(directory))
            return parts;

        foreach (string path in Directory.EnumerateFiles(directory, PartFiles.PlaylistGlob))
        {
            if (!PartFiles.TryParsePlaylist(Path.GetFileName(path), out int part))
            {
                logger.LogWarning(
                    "Ignoring part playlist {File}; part names are p001.m3u8 through p999.m3u8.",
                    path);
                continue;
            }

            if (PartFiles.IsValidPart(part))
                parts.Add((part, path));
            else
                logger.LogWarning("Ignoring part playlist {File}; part numbers start at 1.", path);
        }

        parts.Sort((left, right) => left.Part.CompareTo(right.Part));
        return parts;
    }

    private static Task<string?> TryReadFileAsync(string path, CancellationToken ct) =>
        PartPlaylistReader.TryReadAllTextAsync(path, ct);

    private static long MediaSize(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        long total = 0;
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string name = Path.GetFileName(path);
            if (name == IndexFileName
                || name.EndsWith(".m4s", StringComparison.Ordinal)
                || (name.StartsWith("init_", StringComparison.Ordinal)
                    && name.EndsWith(".mp4", StringComparison.Ordinal)))
            {
                total += new FileInfo(path).Length;
            }
        }

        return total;
    }

    private static string Normalize(string sessionDir) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDir));

}
