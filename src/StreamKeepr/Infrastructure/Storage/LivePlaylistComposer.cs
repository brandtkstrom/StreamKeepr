using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;

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
/// other part comes from the previous snapshot. Composition either appends a suffix or rebuilds
/// through <see cref="PlaylistStitcher.StitchParsed"/>, so an incremental body always equals a
/// full stitch of the same parts.
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
            if (!IsCompletePart(content))
            {
                // A partial or malformed replace must not shrink or corrupt the live body.
                // Keep the last good snapshot and retry on the next refresh.
                return previous ?? LiveSnapshot.Empty(minTargetDuration);
            }

            ParsedPart parsed = PlaylistStitcher.ParsePart(file.PartNumber, content);
            next.Add(new PartSnapshot(
                file.PartNumber,
                file.Length,
                file.LastWriteTicks,
                content,
                parsed.BodyLines,
                parsed.TargetDuration));
            changed = true;
        }

        ImmutableArray<PartSnapshot> built = next.ToImmutable();
        if (previous is null)
            return Rebuild(built, minTargetDuration);

        if (!changed && HasSameParts(previous, built))
            return previous;

        int targetDuration = PlaylistStitcher.EffectiveTargetDuration(
            built.Select(part => part.TargetDuration), minTargetDuration);
        if (targetDuration != previous.TargetDuration)
            return Rebuild(built, minTargetDuration);

        if (!TryAppend(previous, built, out string? body))
            return Rebuild(built, minTargetDuration);

        return new LiveSnapshot(built, body, previous.TargetDuration);
    }

    private static LiveSnapshot Rebuild(
        ImmutableArray<PartSnapshot> parts, int minTargetDuration)
    {
        var parsed = new ParsedPart[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            parsed[i] = parts[i].ToParsed();

        return new LiveSnapshot(
            parts,
            PlaylistStitcher.StitchParsed(parsed, minTargetDuration, final: false),
            PlaylistStitcher.EffectiveTargetDuration(
                parts.Select(part => part.TargetDuration), minTargetDuration));
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

    /// <summary>
    /// Appends the bytes that changed since <paramref name="previous"/> when the previous part
    /// list is a prefix of the new one. Returns false when any body change is not append-only,
    /// in which case the caller rebuilds from the parts already in memory.
    /// </summary>
    private static bool TryAppend(
        LiveSnapshot previous,
        ImmutableArray<PartSnapshot> next,
        [NotNullWhen(true)] out string? body)
    {
        body = null;
        if (next.Length < previous.Parts.Length)
            return false;

        for (int i = 0; i < previous.Parts.Length; i++)
        {
            if (next[i].PartNumber != previous.Parts[i].PartNumber)
                return false;
        }

        var suffix = new StringBuilder();
        bool hasPriorBody = false;

        for (int i = 0; i < next.Length; i++)
        {
            PartSnapshot current = next[i];
            if (i < previous.Parts.Length)
            {
                PartSnapshot prior = previous.Parts[i];
                if (LinesEqual(current.BodyLines, prior.BodyLines))
                {
                    hasPriorBody |= !prior.BodyLines.IsDefaultOrEmpty;
                    continue;
                }

                if (!CanAppendDelta(prior, current))
                    return false;

                if (prior.BodyLines.IsDefaultOrEmpty && hasPriorBody)
                    suffix.Append(PlaylistStitcher.DiscontinuityLine);

                foreach (string line in current.BodyLines[prior.BodyLines.Length..])
                    suffix.Append(line).Append('\n');

                hasPriorBody = true;
            }
            else
            {
                if (current.BodyLines.IsDefaultOrEmpty)
                    continue;

                if (hasPriorBody)
                    suffix.Append(PlaylistStitcher.DiscontinuityLine);

                foreach (string line in current.BodyLines)
                    suffix.Append(line).Append('\n');

                hasPriorBody = true;
            }
        }

        body = previous.Body + suffix;
        return true;
    }

    private static bool CanAppendDelta(PartSnapshot prior, PartSnapshot current) =>
        current.Length > prior.Length
        && current.Content.StartsWith(prior.Content, StringComparison.Ordinal)
        && current.Content.EndsWith('\n')
        && current.BodyLines.Length > prior.BodyLines.Length
        && LinesStartWith(current.BodyLines, prior.BodyLines);

    private static bool LinesStartWith(
        ImmutableArray<string> lines, ImmutableArray<string> prefix)
    {
        if (lines.Length < prefix.Length)
            return false;

        for (int i = 0; i < prefix.Length; i++)
        {
            if (!string.Equals(lines[i], prefix[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool LinesEqual(ImmutableArray<string> left, ImmutableArray<string> right)
    {
        if (left.Length != right.Length)
            return false;

        for (int i = 0; i < left.Length; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// A complete FFmpeg part playlist ends with a line feed and starts with #EXTM3U. Anything
    /// else is a partial write or a contract violation and must not replace the last good body.
    /// </summary>
    private static bool IsCompletePart(string content) =>
        content.EndsWith('\n')
        && content.StartsWith(PlaylistStitcher.ExtM3uTag, StringComparison.Ordinal);
}
