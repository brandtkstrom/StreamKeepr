using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// Reads the manifest and part playlists for a live refresh without the session manifest lock.
/// Each session keeps an immutable part set so a refresh re-reads only the active last part and
/// any new parts; a changed or missing prefix rebuilds the set from scratch.
/// </summary>
public sealed class SessionStoreLivePlaylistIo(
    SessionStore store,
    IOptions<StorageOptions> storage) : ILivePlaylistIo
{
    private readonly ConcurrentDictionary<string, PartSet> _partSets = new(StringComparer.Ordinal);

    public async Task<LivePlaylistRead> ReadAsync(
        RecordingSummary summary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (!RecordingsPath.TryResolve(
                storage.Value.RecordingsRoot, [summary.Channel, summary.Id], out string directory))
        {
            _partSets.TryRemove(summary.Id, out _);
            return LivePlaylistRead.Missing;
        }

        if (!File.Exists(Path.Combine(directory, SessionStore.ManifestFileName)))
        {
            _partSets.TryRemove(summary.Id, out _);
            return LivePlaylistRead.Missing;
        }

        SessionManifest? manifest = await store.TryLoadAsync(directory, ct).ConfigureAwait(false);
        if (manifest is null)
        {
            // The manifest exists but could not be read. Treat it as a refresh failure so the
            // last-known-good body stays live instead of being pruned as a deleted session.
            throw new InvalidDataException(
                $"The session manifest for {summary.Id} is unreadable.");
        }

        if (manifest.Status == SessionStatus.Complete)
        {
            _partSets.TryRemove(summary.Id, out _);
            string? index = await PartPlaylistReader
                .TryReadAllTextAsync(Path.Combine(directory, SessionStore.IndexFileName), ct)
                .ConfigureAwait(false);
            return index is null ? LivePlaylistRead.Missing : LivePlaylistRead.Complete(index);
        }

        IReadOnlyList<(int Part, string Path)> files = store.PartPlaylistFiles(directory);
        PartSet? previous = _partSets.TryGetValue(summary.Id, out PartSet? cached) ? cached : null;
        PartSet set = await BuildAsync(files, previous, ct).ConfigureAwait(false);

        string body = PlaylistStitcher.Stitch(set.Parts, store.MinTargetDuration, final: false);
        _partSets[summary.Id] = set;
        return LivePlaylistRead.Live(body);
    }

    private static async Task<PartSet> BuildAsync(
        IReadOnlyList<(int Part, string Path)> files,
        PartSet? previous,
        CancellationToken ct)
    {
        if (files.Count == 0)
            return PartSet.Empty;

        (int lastPart, string lastPath) = files[^1];
        var lastInfo = new FileInfo(lastPath);
        long lastWriteTicks = lastInfo.LastWriteTimeUtc.Ticks;
        long lastLength = lastInfo.Length;

        if (previous is not null && IsPrefix(previous, files))
        {
            if (previous.LastPart == lastPart
                && previous.LastWriteTicks == lastWriteTicks
                && previous.LastLength == lastLength)
            {
                return previous;
            }

            var builder = ImmutableArray.CreateBuilder<PartPlaylist>(files.Count);

            // Parts before the previous active part are closed and never re-read.
            foreach (PartPlaylist part in previous.Parts)
            {
                if (part.PartNumber >= previous.LastPart)
                    break;

                builder.Add(part);
            }

            // The previous active part may have received its final segments before a new part
            // appeared, so re-read it once along with every new part.
            foreach ((int number, string path) in files)
            {
                if (number < previous.LastPart)
                    continue;

                string content = await PartPlaylistReader.ReadAllTextAsync(path, ct)
                    .ConfigureAwait(false);
                builder.Add(new PartPlaylist(number, content));
            }

            return new PartSet(builder.ToImmutable(), lastPart, lastWriteTicks, lastLength);
        }

        var all = ImmutableArray.CreateBuilder<PartPlaylist>(files.Count);
        foreach ((int number, string path) in files)
        {
            string content = await PartPlaylistReader.ReadAllTextAsync(path, ct)
                .ConfigureAwait(false);
            all.Add(new PartPlaylist(number, content));
        }

        return new PartSet(all.ToImmutable(), lastPart, lastWriteTicks, lastLength);
    }

    private static bool IsPrefix(PartSet previous, IReadOnlyList<(int Part, string Path)> files)
    {
        if (files.Count < previous.Parts.Length)
            return false;

        for (int i = 0; i < previous.Parts.Length; i++)
        {
            if (files[i].Part != previous.Parts[i].PartNumber)
                return false;
        }

        return true;
    }

    /// <summary>
    /// One immutable set of part playlists for a session. Only the active last part may still
    /// change; a published set is replaced as a whole and never mutated.
    /// </summary>
    internal sealed record PartSet(
        ImmutableArray<PartPlaylist> Parts,
        int LastPart,
        long LastWriteTicks,
        long LastLength)
    {
        public static PartSet Empty { get; } = new(ImmutableArray<PartPlaylist>.Empty, 0, 0, 0);
    }
}
