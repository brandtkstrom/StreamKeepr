using System.Globalization;
using System.Text.RegularExpressions;

namespace StreamKeepr.Infrastructure.Media;

/// <summary>
/// Reads Streamlink's Twitch-plugin ad-break notices. Streamlink skips the ad segments, so
/// FFmpeg receives no media until the break ends; the stall watchdog uses the announced length
/// plus a safety margin instead of treating the gap as a stalled stream.
/// </summary>
public static class StreamlinkAdBreak
{
    /// <summary>Extra time after the announced break before the stall watchdog may fire.</summary>
    public static readonly TimeSpan GraceAfterBreak = TimeSpan.FromSeconds(30);

    private static readonly Regex Announcement = new(
        @"Detected advertisement break of (?<seconds>\d+) seconds?",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// True when <paramref name="line"/> announces an advertisement break. The grace deadline is
    /// <paramref name="now"/> plus the announced length plus <see cref="GraceAfterBreak"/>.
    /// </summary>
    public static bool TryGetGraceUntil(
        string line,
        DateTimeOffset now,
        out DateTimeOffset graceUntil)
    {
        ArgumentNullException.ThrowIfNull(line);

        Match match = Announcement.Match(line);
        if (!match.Success
            || !int.TryParse(
                match.Groups["seconds"].ValueSpan,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int seconds))
        {
            graceUntil = default;
            return false;
        }

        graceUntil = now + TimeSpan.FromSeconds(seconds) + GraceAfterBreak;
        return true;
    }
}
