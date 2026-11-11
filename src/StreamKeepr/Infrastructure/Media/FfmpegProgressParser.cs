using System.Globalization;

namespace StreamKeepr.Infrastructure.Media;

/// <summary>One completed FFmpeg <c>-progress</c> block.</summary>
public readonly record struct ProgressSample(
    long TotalBytes,
    TimeSpan MediaTime,
    bool Advanced,
    bool Ended);

/// <summary>
/// Incremental parser for the key=value lines written by FFmpeg's <c>-progress</c> output.
/// A sample is produced only when a line with <c>progress=continue</c> or
/// <c>progress=end</c> arrives. Not thread-safe; one instance belongs to one stdout reader.
/// </summary>
public sealed class FfmpegProgressParser
{
    private long _bytes;
    private long _mediaMicroseconds;
    private long _lastReportedMicroseconds;

    public ProgressSample? Feed(string line)
    {
        if (string.IsNullOrEmpty(line))
            return null;

        ReadOnlySpan<char> span = line.AsSpan().TrimEnd('\r');
        int separator = span.IndexOf('=');
        if (separator <= 0)
            return null;

        ReadOnlySpan<char> key = span[..separator];
        ReadOnlySpan<char> value = span[(separator + 1)..];

        if (key.SequenceEqual("total_size"))
        {
            // "N/A" is normal for the HLS muxer; keep the last reported value.
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes)
                && bytes >= 0)
            {
                _bytes = bytes;
            }

            return null;
        }

        if (key.SequenceEqual("out_time_us"))
        {
            // "N/A" appears before the first packet.
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long microseconds)
                && microseconds >= 0)
            {
                _mediaMicroseconds = microseconds;
            }

            return null;
        }

        if (!key.SequenceEqual("progress"))
            return null;

        bool advanced = _mediaMicroseconds > _lastReportedMicroseconds;
        _lastReportedMicroseconds = Math.Max(_lastReportedMicroseconds, _mediaMicroseconds);

        return new ProgressSample(
            _bytes,
            TimeSpan.FromMicroseconds(_mediaMicroseconds),
            advanced,
            value.SequenceEqual("end"));
    }
}
