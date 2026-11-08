using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamRecorder;

public static partial class Extensions
{
    // Windows' invalid set is a superset of what macOS/Linux reject (Unix only
    // forbids '/' and NUL), so one set yields names that are valid everywhere.
    // Path.GetInvalidFileNameChars() is deliberately NOT used: on Linux it returns
    // only '/' and '\0', which would let ':' and friends through and break on Windows.
    private static readonly SearchValues<char> InvalidChars = SearchValues.Create("<>:\"/\\|?*" + new string(Enumerable.Range(0, 32).Select(i => (char) i).ToArray()));

    // Reserved on Windows even with an extension ("CON.txt") and even with trailing
    // spaces in the stem ("CON .txt"). Includes COM0/LPT0 and the superscript digits
    // from Microsoft's current naming docs, plus the console aliases CONIN$/CONOUT$.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"
    };

    // .NET 9+: lets us look up a ReadOnlySpan<char> stem without allocating a string.
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> ReservedLookup =
        Reserved.GetAlternateLookup<ReadOnlySpan<char>>();

    // ext4 / APFS: 255 *bytes* (UTF-8). NTFS: 255 UTF-16 code units. Respect both.
    // These are fixed filesystem limits, not tuning knobs, so they aren't parameters.
    private const int MaxBytes = 255;
    private const int MaxUtf16Units = 255;

    // Extensions are always 3–4 characters ("pdf", "docx"), not counting the dot.
    // Anything longer after the last dot is treated as part of the name.
    private const int MaxExtensionLength = 4;

    // Source-generated (the class is already partial), so the pattern isn't
    // parsed on every call. .NET's \s covers Unicode spaces (\p{Z}) such as NBSP.
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    /// <summary>
    /// Converts arbitrary text into a single path segment that is valid on Windows,
    /// macOS, and Linux.
    /// </summary>
    /// <param name="str">The filename to sanitize, including its extension. May be null.</param>
    /// <returns>The sanitized filename, or "" if nothing usable is left (e.g. "???" or "...").</returns>
    public static string Sanitize(this string? str)
    {
        if (string.IsNullOrWhiteSpace(str))
        {
            return "";
        }

        // 1. Filter by code point (Rune), not by char. This also repairs invalid UTF-16:
        //    EnumerateRunes yields U+FFFD for unpaired surrogates, which we drop.
        //    Doing this BEFORE Normalize matters, because Normalize throws on lone surrogates.
        var sb = new StringBuilder(str.Length);

        foreach (var rune in str.EnumerateRunes())
        {
            // Invalid filename chars (including C0 controls like \t and \n) become spaces,
            // so "a/b" -> "a b". The user's own underscores are left alone.
            if (rune.IsBmp && InvalidChars.Contains((char) rune.Value))
            {
                sb.Append(' ');
                continue;
            }

            // Repaired surrogates (and literal U+FFFD) carry no information.
            if (rune == Rune.ReplacementChar)
            {
                continue;
            }

            // Some Format chars carry meaning and must survive the filter below.
            if (IsMeaningfulFormatChar(rune))
            {
                sb.Append(rune);
                continue;
            }

            // Drop C1 controls/DEL, invisible format chars (bidi overrides like U+202E,
            // BOM, soft hyphen), and private-use chars from every plane.
            if (Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.PrivateUse)
            {
                continue;
            }

            sb.Append(rune);
        }

        // 2. NFC: "é" (U+00E9) vs "e" + U+0301 render the same but are different bytes.
        //    APFS compares normalization-insensitively, but NTFS and ext4 do NOT, so
        //    without this the same title could produce two distinct files.
        //    Normalizing after filtering means removals can't leave a non-NFC sequence,
        //    and the later steps (whitespace collapse, grapheme truncation) preserve NFC.
        var cleaned = sb.ToString().Normalize(NormalizationForm.FormC);

        // 3. Collapse whitespace runs to one space ("a   b" == "a b").
        cleaned = WhitespaceRun().Replace(cleaned, " ");

        // 4. Leading dots would make a hidden file on macOS/Linux; trailing dots/spaces
        //    are silently stripped by Windows, so strip them ourselves so the name we
        //    log matches what's on disk. This also turns "." and ".." into "".
        cleaned = cleaned.TrimStart('.', ' ').TrimEnd('.', ' ');

        if (cleaned.Length == 0)
        {
            return "";
        }

        // 5. Split off the extension so truncation never eats it. The dot is included
        //    in ext, so ".docx" is 5 characters: the dot plus at most 4.
        var stem = cleaned;
        var ext = "";
        int dot = cleaned.LastIndexOf('.');

        bool looksLikeExtension = dot > 0
                                  && cleaned.Length - dot - 1 <= MaxExtensionLength
                                  && !cleaned.AsSpan(dot).Contains(' ');

        if (looksLikeExtension)
        {
            var candidateStem = cleaned[..dot].TrimEnd('.', ' ');

            // "...pdf" style input has no real name, so keep it whole rather than
            // producing a bare ".pdf" (a hidden file on macOS/Linux).
            if (candidateStem.Length > 0)
            {
                stem = candidateStem;
                ext = cleaned[dot..];
            }
        }

        // 6. Truncate, then fix up reserved names / leading '-'. If a prefix is needed,
        //    rebuild with the prefix counted against the budget so we never exceed it.
        var result = Build(stem, ext, prefix: "");

        if (NeedsPrefix(result))
        {
            result = Build(stem, ext, prefix: "_");
        }

        return result;
    }

    // ZWJ (U+200D) joins compound emoji like 👨‍👩‍👧. ZWNJ (U+200C) changes rendering and
    // meaning in Persian, Hindi, and other scripts. Tag characters (U+E0020–U+E007F)
    // spell out subdivision flags such as England and Scotland.
    private static bool IsMeaningfulFormatChar(Rune rune)
    {
        return rune.Value is 0x200D or 0x200C or (>= 0xE0020 and <= 0xE007F);
    }

    // Assembles prefix + truncated stem + extension within both budgets.
    private static string Build(string stem, string ext, string prefix)
    {
        int unitBudget = MaxUtf16Units - prefix.Length - ext.Length;
        int byteBudget = MaxBytes - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(ext);

        // Trailing dots/spaces can reappear at the cut point, so trim again.
        var truncated = TruncateGraphemes(stem, unitBudget, byteBudget).TrimEnd('.', ' ');

        // An empty stem would leave just ".ext", which is a hidden file on Unix.
        return truncated.Length == 0 ? "" : prefix + truncated + ext;
    }

    // True for Windows device names (checked on the stem before the first dot,
    // ignoring trailing spaces) and for names starting with '-', which shell tools
    // can misread as an option ("rm -rf.txt").
    private static bool NeedsPrefix(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        if (name[0] == '-')
        {
            return true;
        }

        int dot = name.IndexOf('.');
        var stem = (dot < 0 ? name.AsSpan() : name.AsSpan(0, dot)).TrimEnd(' ');
        return ReservedLookup.Contains(stem);
    }

    // Cuts on extended grapheme cluster boundaries (UAX #29, implemented by StringInfo
    // in .NET 5+), so emoji sequences, flags, and base letters with combining marks are
    // never split. Caps both UTF-16 units and UTF-8 bytes. Allocates only the result.
    private static string TruncateGraphemes(string s, int maxUtf16, int maxBytes)
    {
        if (s.Length <= maxUtf16 && Encoding.UTF8.GetByteCount(s) <= maxBytes)
        {
            return s;
        }

        ReadOnlySpan<char> rest = s;
        int units = 0;
        int bytes = 0;

        while (!rest.IsEmpty)
        {
            int clusterLength = StringInfo.GetNextTextElementLength(rest);
            int clusterBytes = Encoding.UTF8.GetByteCount(rest[..clusterLength]);

            if (units + clusterLength > maxUtf16 || bytes + clusterBytes > maxBytes)
            {
                break;
            }

            units += clusterLength;
            bytes += clusterBytes;
            rest = rest[clusterLength..];
        }

        return s[..units];
    }
}