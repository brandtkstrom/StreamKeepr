using StreamKeepr.Infrastructure.Media;

namespace StreamKeepr.Tests.Media;

public sealed class FfmpegArgsTests
{
    [Fact]
    public void ForPart_matches_the_recorder_contract()
    {
        const string directory = "/recordings/streamer/session";

        IReadOnlyList<string> args = FfmpegArgs.ForPart(directory, 7, 6);

        string[] expected =
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
            "-hls_time", "6",
            "-hls_list_size", "0",
            "-hls_playlist_type", "event",
            "-hls_flags", "+temp_file+independent_segments+program_date_time",
            "-hls_fmp4_init_filename", "init_007.mp4",
            "-hls_segment_filename", Path.Combine(directory, "p007_%05d.m4s"),
            Path.Combine(directory, "p007.m3u8"),
        ];

        Assert.Equal(expected, args);
    }

    [Fact]
    public void ForPart_always_includes_the_aac_bitstream_filter()
    {
        IReadOnlyList<string> args = FfmpegArgs.ForPart("/recordings/session", 1, 2);

        int index = args.ToList().IndexOf("-bsf:a");
        Assert.True(index >= 0, "-bsf:a must always be present");
        Assert.Equal("aac_adtstoasc", args[index + 1]);
        Assert.Contains("-c", args);
        Assert.Equal("copy", args[args.ToList().IndexOf("-c") + 1]);
    }

    [Fact]
    public void ForPart_uses_invariant_segment_seconds()
    {
        IReadOnlyList<string> args = FfmpegArgs.ForPart("/recordings/session", 1, 10);
        List<string> list = args.ToList();

        Assert.Equal("10", list[list.IndexOf("-hls_time") + 1]);
    }
}
