using System.Globalization;

namespace StreamKeepr.Infrastructure.Media;

/// <summary>Builds the FFmpeg command line for one fMP4 HLS part.</summary>
public static class FfmpegArgs
{
    public static IReadOnlyList<string> ForPart(string directory, int part, int segmentSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfNegative(part);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentSeconds);

        return
        [
            "-hide_banner", "-loglevel", "warning", "-nostats",
            "-progress", "pipe:1", "-stats_period", "5",
            "-fflags", "+genpts+discardcorrupt",
            "-i", "pipe:0",
            "-map", "0:v", "-map", "0:a?",
            "-c", "copy",
            "-bsf:a", "aac_adtstoasc",
            "-f", "hls",
            "-hls_segment_type", "fmp4",
            "-hls_time", segmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-hls_list_size", "0",
            "-hls_playlist_type", "event",
            "-hls_flags", "+temp_file+independent_segments+program_date_time",
            "-hls_fmp4_init_filename", PartFiles.Init(part),
            "-hls_segment_filename", Path.Combine(directory, PartFiles.SegmentPattern(part)),
            Path.Combine(directory, PartFiles.Playlist(part)),
        ];
    }
}
