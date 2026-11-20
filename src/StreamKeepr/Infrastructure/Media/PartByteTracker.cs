namespace StreamKeepr.Infrastructure.Media;

/// <summary>
/// Incrementally totals the bytes of one part's init and completed media segments from its EVENT
/// playlist. The playlist is re-read only when its length or write time changes, and each newly
/// seen init or segment name is stat'd once. Metadata failures never escape the progress path.
/// </summary>
public sealed class PartByteTracker
{
    private readonly string _directory;
    private readonly int _part;
    private readonly string _playlistPath;
    private readonly string _initName;
    private readonly IPartByteSource _source;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    private long _total;
    private long _highWater;
    private PlaylistStamp? _stamp;
    private bool _forceRead = true;
    private bool _retryNextRead;

    public PartByteTracker(string directory, int part)
        : this(directory, part, PhysicalPartByteSource.Instance)
    {
    }

    internal PartByteTracker(string directory, int part, IPartByteSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(part, PartFiles.FirstPart);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(part, PartFiles.LastPart);
        ArgumentNullException.ThrowIfNull(source);

        _directory = directory;
        _part = part;
        _playlistPath = Path.Combine(directory, PartFiles.Playlist(part));
        _initName = PartFiles.Init(part);
        _source = source;
    }

    /// <summary>The last successfully accumulated total.</summary>
    public long Total => _total;

    /// <summary>
    /// Returns the part's byte total for live progress. The first call reads the playlist; later
    /// calls re-read it only after it changed. A metadata failure returns the last known total and
    /// schedules a retry for the next sample, so it can never fault FFmpeg's stdout pipe.
    /// </summary>
    public long Sample()
    {
        try
        {
            PlaylistStamp? stamp = _source.GetPlaylistStamp(_playlistPath);
            if (_forceRead || stamp != _stamp)
                Refresh(stamp);
        }
        catch (Exception ex) when (IsMetadataFailure(ex))
        {
            _forceRead = true;
            _stamp = null;
        }

        _highWater = Math.Max(_highWater, _total);
        return _highWater;
    }

    /// <summary>
    /// One authoritative scan of the part's init and completed segments, used when the part ends.
    /// Callers fall back to <see cref="Total"/> if the scan fails.
    /// </summary>
    public long Reconcile() => PartFiles.TotalBytes(_directory, _part);

    private void Refresh(PlaylistStamp? stamp)
    {
        _retryNextRead = false;
        bool metadataReady = stamp is null;
        try
        {
            if (stamp is not null)
            {
                string playlist = _source.ReadPlaylist(_playlistPath);
                metadataReady = true;
                Parse(playlist);
            }
        }
        finally
        {
            // The init has a known name, so it still counts when the map tag is missing,
            // malformed, or the playlist is absent. Skip it only when the read itself failed.
            if (metadataReady)
                TrackInit();
        }

        _stamp = stamp;
        _forceRead = _retryNextRead;
    }

    private void Parse(string playlist)
    {
        foreach (string raw in playlist.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                TrackMapUri(line);
                continue;
            }

            if (line[0] == '#')
                continue;

            TrackName(line);
        }
    }

    private void TrackMapUri(string line)
    {
        const string key = "URI=\"";
        int start = line.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
            return;

        start += key.Length;
        int end = line.IndexOf('"', start);
        if (end < 0)
            return;

        TrackName(line[start..end]);
    }

    private void TrackName(string candidate)
    {
        if (!PartFiles.TryGetBareFileName(candidate, out string fileName))
            return;

        if (PartFiles.TryParseInit(fileName, out int initPart))
        {
            if (initPart == _part)
                TrackFile(fileName, missingIsTransient: false);
            return;
        }

        if (PartFiles.TryParseSegment(fileName, out int segmentPart)
            && segmentPart == _part)
        {
            TrackFile(fileName, missingIsTransient: true);
        }
    }

    private void TrackInit() => TrackFile(_initName, missingIsTransient: false);

    private void TrackFile(string fileName, bool missingIsTransient)
    {
        if (!_seen.Add(fileName))
            return;

        try
        {
            _total += _source.GetFileLength(Path.Combine(_directory, fileName));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Leave the name unseen so a later sample can retry it. A segment can be listed a
            // moment before its rename is visible; the rest of the playlist still counts. A
            // missing init is picked up by the next playlist refresh unless this was a segment.
            _seen.Remove(fileName);
            if (missingIsTransient)
                _retryNextRead = true;
        }
        catch
        {
            _seen.Remove(fileName);
            throw;
        }
    }

    private static bool IsMetadataFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException;
}

internal readonly record struct PlaylistStamp(long Length, DateTime LastWriteTimeUtc);

/// <summary>File-system boundary for <see cref="PartByteTracker"/>; tests supply a probe.</summary>
internal interface IPartByteSource
{
    PlaylistStamp? GetPlaylistStamp(string path);

    string ReadPlaylist(string path);

    long GetFileLength(string path);
}

internal sealed class PhysicalPartByteSource : IPartByteSource
{
    public static PhysicalPartByteSource Instance { get; } = new();

    private PhysicalPartByteSource()
    {
    }

    public PlaylistStamp? GetPlaylistStamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists
            ? new PlaylistStamp(info.Length, info.LastWriteTimeUtc)
            : null;
    }

    public string ReadPlaylist(string path)
    {
        // Share delete so FFmpeg's atomic playlist rename never waits for this reader.
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public long GetFileLength(string path) => new FileInfo(path).Length;
}
