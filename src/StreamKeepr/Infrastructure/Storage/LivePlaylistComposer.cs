using System.Collections.Immutable;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>Fresh metadata for one part file, cheap enough to collect on every refresh.</summary>
internal readonly record struct PartFileObservation(
    int PartNumber,
    string Path,
    long Length,
    long LastWriteTicks);

/// <summary>
/// Builds a live snapshot from fresh part-file metadata. Only a part that may still be growing
/// (the active last part, or the previous last part after a new part appears) is read; every
/// other part comes from the previous snapshot. Any change renders the live index from the
/// parsed parts in memory through <see cref="PlaylistStitcher.Render"/>, the same renderer as
/// the session index, so the live index always equals a full stitch of the same parts.
/// </summary>
internal static class LivePlaylistComposer
{
    public static async Task<LiveSnapshot> BuildAsync(
        LiveSnapshot? previous,
        IReadOnlyList<PartFileObservation> files,
        int minTargetDuration,
        IPartPlaylistSource source,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(minTargetDuration, 1);

        if (files.Count == 0)
            return LiveSnapshot.Empty(minTargetDuration);

        var previousByNumber = new Dictionary<int, PartSnapshot>();
        if (previous is not null)
        {
            foreach (PartSnapshot part in previous.Parts)
                previousByNumber[part.PartNumber] = part;
        }

        int previousLastPart = previous is { Parts.Length: > 0 }
            ? previous.Parts[^1].PartNumber
            : 0;
        int lastIndex = files.Count - 1;

        var next = ImmutableArray.CreateBuilder<PartSnapshot>(files.Count);
        bool changed = false;

        for (int i = 0; i < files.Count; i++)
        {
            PartFileObservation file = files[i];
            bool isLast = i == lastIndex;
            previousByNumber.TryGetValue(file.PartNumber, out PartSnapshot? known);

            if (known is not null)
            {
                if (known.Matches(file.Length, file.LastWriteTicks))
                {
                    next.Add(known);
                    continue;
                }

                // Only the active last part may still grow. Once a new part exists, the
                // previous last part may have received its final segments before it closed,
                // so it is read once more; every other closed part keeps its cached body.
                bool mayHaveGrown = isLast || file.PartNumber == previousLastPart;
                if (!mayHaveGrown)
                {
                    next.Add(known with
                    {
                        Length = file.Length,
                        LastWriteTicks = file.LastWriteTicks,
                    });
                    changed = true;
                    continue;
                }
            }

            string content = await source.ReadAsync(file.Path, ct).ConfigureAwait(false);
            if (!PlaylistStitcher.IsCompletePart(content))
            {
                // A partial or malformed replace must not shrink or corrupt the live body.
                // Keep the last good snapshot and retry on the next refresh.
                return previous ?? LiveSnapshot.Empty(minTargetDuration);
            }

            next.Add(new PartSnapshot(
                PlaylistStitcher.ParsePart(file.PartNumber, content),
                file.Length,
                file.LastWriteTicks));
            changed = true;
        }

        ImmutableArray<PartSnapshot> built = next.ToImmutable();
        if (previous is not null && !changed && HasSameParts(previous, built))
            return previous;

        return Render(built, minTargetDuration);
    }

    private static LiveSnapshot Render(
        ImmutableArray<PartSnapshot> parts, int minTargetDuration)
    {
        var parsed = new ParsedPart[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            parsed[i] = parts[i].Parsed;

        return new LiveSnapshot(
            parts, PlaylistStitcher.Render(parsed, minTargetDuration, final: false));
    }

    private static bool HasSameParts(
        LiveSnapshot previous, ImmutableArray<PartSnapshot> parts)
    {
        if (parts.Length != previous.Parts.Length)
            return false;

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].PartNumber != previous.Parts[i].PartNumber)
                return false;
        }

        return true;
    }
}
