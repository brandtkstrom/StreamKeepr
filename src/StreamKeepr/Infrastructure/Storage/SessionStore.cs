using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Media;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Owns the per-session manifest, locking, atomic writes, and playlist stitching. The recorder
/// writes the part files; this store writes <c>session.json</c> and <c>index.m3u8</c>.
/// </summary>
/// <remarks>
/// Every mutation takes <see cref="LockFor"/> for its session folder. Public methods never call
/// another locking method while holding a lock; the <c>*Core</c> helpers assume the caller holds it.
/// </remarks>
public sealed class SessionStore(
    IOptions<RecorderOptions> recorder,
    RecordingLibrary library,
    TimeProvider clock,
    ILogger<SessionStore> logger)
{
    public const int ManifestVersion = 1;
    public const string ManifestFileName = "session.json";
    public const string IndexFileName = "index.m3u8";
    public const string ThumbnailFileName = "thumb.jpg";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LiveIndex> _live = new(StringComparer.Ordinal);

    private int MinTargetDuration => 2 * recorder.Value.SegmentSeconds;

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
    /// Creates a session, or reopens the existing one. Never overwrites an existing manifest or
    /// its metadata history.
    /// </summary>
    public async Task OpenAsync(Session session, LiveStream live, CancellationToken ct)
    {
        string directory = Normalize(session.Directory);
        var gate = LockFor(directory);
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(Path.Combine(directory, ManifestFileName)))
            {
                await ReopenCoreAsync(directory, ct);
                return;
            }

            Directory.CreateDirectory(directory);
            var manifest = new SessionManifest(
                ManifestVersion,
                session.Id,
                session.Channel,
                session.StreamId,
                session.StartedAt,
                SessionStatus.Recording,
                ImmutableList.Create(new MetadataChange(clock.GetUtcNow(), live.Title, live.Game)));

            await WriteManifestAsync(directory, manifest, ct);
            _live.TryRemove(directory, out _);
            await UpsertLibraryAsync(directory, ct);
            logger.LogInformation("Opened session {SessionId} in {Directory}.", session.Id, directory);
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
            await ReopenCoreAsync(directory, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Appends a title/game change. A repeated observation with the same title and game writes
    /// nothing. The latest entry is the session's current title and game.
    /// </summary>
    public async Task AppendMetadataAsync(Session session, LiveStream live, CancellationToken ct)
    {
        string directory = Normalize(session.Directory);
        var gate = LockFor(directory);
        await gate.WaitAsync(ct);
        try
        {
            var manifest = await ReadManifestCoreAsync(directory, ct);
            if (manifest.Metadata.IsEmpty)
                throw new InvalidDataException($"Session manifest in {directory} has no metadata.");

            var latest = manifest.Metadata[^1];
            if (latest.Title == live.Title && latest.Game == live.Game)
                return;

            var updated = manifest with
            {
                Metadata = manifest.Metadata.Add(
                    new MetadataChange(clock.GetUtcNow(), live.Title, live.Game)),
            };
            await WriteManifestAsync(directory, updated, ct);
            await UpsertLibraryAsync(directory, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Stitches every non-empty part into <c>index.m3u8</c> with one ENDLIST and marks the manifest
    /// Complete. A session with no media is deleted.
    /// </summary>
    public async Task FinalizeAsync(Session session, CancellationToken ct)
    {
        string directory = Normalize(session.Directory);
        var gate = LockFor(directory);
        await gate.WaitAsync(ct);
        try
        {
            await FinalizeCoreAsync(directory, ct);
        }
        finally
        {
            _live.TryRemove(directory, out _);
            gate.Release();
        }
    }

    /// <summary>
    /// Moves a finished thumbnail image into the session as <c>thumb.jpg</c> and refreshes the
    /// library. The thumbnail is written once and never replaced, because <c>/media</c> serves it
    /// as immutable. <paramref name="imagePath"/> must be on the recordings volume so the move is a
    /// rename. The image is consumed either way: returns false (and deletes it) when the session
    /// no longer exists or already has a thumbnail.
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
            // A delete holds this lock while it removes the folder, so the check is race-free.
            if (!File.Exists(Path.Combine(directory, ManifestFileName)) || File.Exists(target))
            {
                File.Delete(imagePath);
                return false;
            }

            File.Move(imagePath, target, overwrite: false);
            await UpsertLibraryAsync(directory, ct);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Serves the playlist for a recording session: a cache hit is lock-free; a cache miss reads
    /// the parts under the session lock. A Complete session always returns the finalized index.
    /// Returns null when the session has no readable manifest or finalized index.
    /// </summary>
    public async Task<string?> StitchLiveAsync(string sessionDir, CancellationToken ct)
    {
        string directory = Normalize(sessionDir);
        if (!File.Exists(Path.Combine(directory, ManifestFileName)))
            return null;

        string key = CacheKey(PartPlaylistFiles(directory));
        if (_live.TryGetValue(directory, out var cached) && cached.Key == key)
            return cached.Body;

        var gate = LockFor(directory);
        await gate.WaitAsync(ct);
        try
        {
            key = CacheKey(PartPlaylistFiles(directory));
            if (_live.TryGetValue(directory, out cached) && cached.Key == key)
                return cached.Body;

            var manifest = await TryLoadAsync(directory, ct);
            if (manifest is null)
                return null;

            string? body;
            if (manifest.Status == SessionStatus.Complete)
            {
                body = await TryReadFileAsync(Path.Combine(directory, IndexFileName), ct);
                if (body is null)
                    return null;
            }
            else
            {
                var parts = await ReadPartsAsync(directory, ct);
                body = PlaylistStitcher.Stitch(parts, MinTargetDuration, final: false);
            }

            _live[directory] = new LiveIndex(key, body);
            return body;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reads the manifest and derives the library summary. Returns null when the manifest is
    /// missing or unreadable. Chapter history comes from the metadata after the first entry.
    /// </summary>
    public async Task<RecordingSummary?> SummarizeAsync(string sessionDir, CancellationToken ct)
    {
        string directory = Normalize(sessionDir);
        var manifest = await TryLoadAsync(directory, ct);
        if (manifest is null)
            return null;

        var parts = await ReadPartsAsync(directory, ct);
        string? playlist = null;

        if (manifest.Status == SessionStatus.Complete)
            playlist = await TryReadFileAsync(Path.Combine(directory, IndexFileName), ct);

        playlist ??= PlaylistStitcher.Stitch(parts, MinTargetDuration, final: false);

        var latest = manifest.Metadata.Count > 0
            ? manifest.Metadata[^1]
            : throw new InvalidDataException($"Session manifest in {directory} has no metadata.");

        double? duration = manifest.Status == SessionStatus.Complete
            ? (File.Exists(Path.Combine(directory, IndexFileName))
                ? PlaylistStitcher.TotalDuration(playlist)
                : manifest.DurationSeconds)
            : null;

        return new RecordingSummary(
            manifest.Id,
            manifest.Channel,
            latest.Title,
            latest.Game,
            manifest.StartedAt,
            manifest.EndedAt,
            IsRecording: manifest.Status == SessionStatus.Recording,
            duration,
            MediaSize(directory),
            PartCount: parts.Count,
            SegmentCount: PlaylistStitcher.SegmentCount(playlist),
            HasThumbnail: File.Exists(Path.Combine(directory, ThumbnailFileName)),
            Chapters: PlaylistStitcher.Chapters(playlist, manifest.Metadata),
            HasChat: ChatLogStore.HasChat(directory, logger));
    }

    /// <summary>
    /// Reads a session manifest without taking the lock. Used at startup and for read-only
    /// summary projections; a missing or corrupt manifest returns null and never deletes media.
    /// </summary>
    public async Task<SessionManifest?> TryLoadAsync(string sessionDir, CancellationToken ct)
    {
        string directory = Normalize(sessionDir);
        if (!File.Exists(Path.Combine(directory, ManifestFileName)))
            return null;

        try
        {
            return await ReadManifestCoreAsync(directory, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or NotSupportedException)
        {
            logger.LogWarning(ex, "Skipping unreadable session manifest in {Directory}.", directory);
            return null;
        }
    }

    private async Task ReopenCoreAsync(string directory, CancellationToken ct)
    {
        var manifest = await ReadManifestCoreAsync(directory, ct);
        var reopened = manifest with
        {
            Status = SessionStatus.Recording,
            EndedAt = null,
            DurationSeconds = null,
            SizeBytes = null,
        };

        string indexPath = Path.Combine(directory, IndexFileName);
        if (File.Exists(indexPath))
            File.Delete(indexPath);

        await WriteManifestAsync(directory, reopened, ct);
        _live.TryRemove(directory, out _);
        await UpsertLibraryAsync(directory, ct);
        logger.LogInformation("Reopened session {SessionId} in {Directory}.", reopened.Id, directory);
    }

    private async Task FinalizeCoreAsync(string directory, CancellationToken ct)
    {
        var manifest = await ReadManifestCoreAsync(directory, ct);
        var parts = await ReadPartsAsync(directory, ct);

        if (parts.Count == 0)
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);

            library.Remove(manifest.Id);
            logger.LogInformation("Deleted empty session {SessionId}.", manifest.Id);
            return;
        }

        string index = PlaylistStitcher.Stitch(parts, MinTargetDuration, final: true);
        await WriteAtomicAsync(Path.Combine(directory, IndexFileName), index, ct);

        var complete = manifest with
        {
            Status = SessionStatus.Complete,
            EndedAt = manifest.EndedAt ?? clock.GetUtcNow(),
            DurationSeconds = PlaylistStitcher.TotalDuration(index),
            SizeBytes = MediaSize(directory),
        };
        await WriteManifestAsync(directory, complete, ct);
        await UpsertLibraryAsync(directory, ct);
        logger.LogInformation("Finalized session {SessionId} in {Directory}.", complete.Id, directory);
    }

    private async Task UpsertLibraryAsync(string directory, CancellationToken ct)
    {
        try
        {
            if (await SummarizeAsync(directory, ct) is { } summary)
                library.Upsert(summary);
        }
        catch (Exception ex) when (ex is FormatException or IOException
            or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            // The manifest mutation already succeeded. A projection failure must not
            // turn a recording command into a failure; recovery or the next update
            // will rebuild the summary once the media is readable.
            logger.LogWarning(
                ex, "Could not update the recording library for {Directory}.", directory);
        }
    }

    private async Task<SessionManifest> ReadManifestCoreAsync(string directory, CancellationToken ct)
    {
        string path = Path.Combine(directory, ManifestFileName);
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);

        var manifest = await JsonSerializer.DeserializeAsync<SessionManifest>(stream, Json, ct)
            ?? throw new InvalidDataException($"Empty session manifest in {directory}.");

        if (manifest.Version != ManifestVersion)
        {
            throw new InvalidDataException(
                $"Unsupported session manifest version {manifest.Version} in {directory}.");
        }

        if (manifest.Metadata is not { Count: > 0 })
            throw new InvalidDataException($"Session manifest in {directory} has no metadata.");

        return manifest;
    }

    /// <summary>Serializes a manifest with the store's authoritative schema options.</summary>
    internal static string SerializeManifest(SessionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, Json);
    }

    private static Task WriteManifestAsync(
        string directory, SessionManifest manifest, CancellationToken ct) =>
        WriteAtomicAsync(
            Path.Combine(directory, ManifestFileName),
            SerializeManifest(manifest),
            ct);

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        string temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, ct);
        File.Move(temp, path, overwrite: true);
    }

    private async Task<List<PartPlaylist>> ReadPartsAsync(string directory, CancellationToken ct)
    {
        var parts = new List<PartPlaylist>();

        foreach (var (number, path) in PartPlaylistFiles(directory))
        {
            string content = await File.ReadAllTextAsync(path, ct);
            if (HasSegments(content))
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

        foreach (string path in Directory.EnumerateFiles(directory, "p*.m3u8"))
        {
            if (!PartFiles.TryParsePlaylist(Path.GetFileName(path), out int part))
            {
                logger.LogWarning(
                    "Ignoring part playlist {File}; part names are p001.m3u8 through p999.m3u8.",
                    path);
                continue;
            }

            if (part is >= 1 and <= 999)
                parts.Add((part, path));
            else
                logger.LogWarning("Ignoring part playlist {File}; part numbers start at 1.", path);
        }

        parts.Sort((left, right) => left.Part.CompareTo(right.Part));
        return parts;
    }

    private static bool HasSegments(string playlist)
    {
        foreach (string raw in playlist.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length > 0 && line[0] != '#')
                return true;
        }

        return false;
    }

    private static string CacheKey(IReadOnlyList<(int Part, string Path)> parts)
    {
        if (parts.Count == 0)
            return "empty";

        string last = parts[^1].Path;
        var info = new FileInfo(last);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{last}|{info.LastWriteTimeUtc.Ticks}|{info.Length}");
    }

    private static async Task<string?> TryReadFileAsync(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

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

    private sealed record LiveIndex(string Key, string Body);
}
