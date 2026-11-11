using StreamKeepr.Infrastructure.Media;

namespace StreamKeepr.Tests.Media;

public sealed class FfmpegProgressParserTests
{
    [Fact]
    public void Total_size_na_still_emits_a_sample_when_progress_continues()
    {
        var parser = new FfmpegProgressParser();

        Assert.Null(parser.Feed("total_size=N/A"));
        Assert.Null(parser.Feed("out_time_us=N/A"));

        ProgressSample sample = Assert.IsType<ProgressSample>(parser.Feed("progress=continue"));

        Assert.Equal(0, sample.TotalBytes);
        Assert.Equal(TimeSpan.Zero, sample.MediaTime);
        Assert.False(sample.Advanced);
        Assert.False(sample.Ended);
    }

    [Fact]
    public void Out_time_us_advances_the_media_time_and_marks_the_sample_advanced()
    {
        var parser = new FfmpegProgressParser();

        Assert.Null(parser.Feed("total_size=N/A"));
        Assert.Null(parser.Feed("out_time_us=2500000"));

        ProgressSample sample = Assert.IsType<ProgressSample>(parser.Feed("progress=continue"));

        Assert.Equal(TimeSpan.FromSeconds(2.5), sample.MediaTime);
        Assert.True(sample.Advanced);
        Assert.False(sample.Ended);
    }

    [Fact]
    public void Unchanged_out_time_us_is_not_advanced()
    {
        var parser = new FfmpegProgressParser();
        parser.Feed("out_time_us=1000000");
        Assert.True(parser.Feed("progress=continue")!.Value.Advanced);

        ProgressSample sample = Assert.IsType<ProgressSample>(parser.Feed("progress=continue"));

        Assert.Equal(TimeSpan.FromSeconds(1), sample.MediaTime);
        Assert.False(sample.Advanced);
    }

    [Fact]
    public void Progress_end_is_reported_on_the_final_block()
    {
        var parser = new FfmpegProgressParser();

        Assert.Null(parser.Feed("total_size=N/A"));
        Assert.Null(parser.Feed("out_time_us=5034667"));

        ProgressSample sample = Assert.IsType<ProgressSample>(parser.Feed("progress=end"));

        Assert.Equal(TimeSpan.FromSeconds(5.034667), sample.MediaTime);
        Assert.True(sample.Advanced);
        Assert.True(sample.Ended);
    }

    [Fact]
    public void A_part_with_only_the_final_progress_block_still_advances()
    {
        var parser = new FfmpegProgressParser();
        parser.Feed("out_time_us=4000000");

        ProgressSample sample = Assert.IsType<ProgressSample>(parser.Feed("progress=end"));

        Assert.Equal(TimeSpan.FromSeconds(4), sample.MediaTime);
        Assert.True(sample.Advanced);
        Assert.True(sample.Ended);
    }

    [Fact]
    public void Numeric_total_size_is_retained_across_a_later_na()
    {
        var parser = new FfmpegProgressParser();
        parser.Feed("total_size=12345");
        parser.Feed("total_size=N/A");
        parser.Feed("out_time_us=1000000");

        ProgressSample sample = Assert.IsType<ProgressSample>(parser.Feed("progress=continue"));

        Assert.Equal(12345, sample.TotalBytes);
    }

    [Fact]
    public void Lines_without_a_recognised_key_are_ignored()
    {
        var parser = new FfmpegProgressParser();

        Assert.Null(parser.Feed(""));
        Assert.Null(parser.Feed("frame=75"));
        Assert.Null(parser.Feed("bitrate=N/A"));
        Assert.Null(parser.Feed("=value"));
        Assert.Null(parser.Feed("no separator"));
    }

    [Fact]
    public void Crlf_line_endings_are_trimmed()
    {
        var parser = new FfmpegProgressParser();

        parser.Feed("out_time_us=1000000\r");
        ProgressSample sample = Assert.IsType<ProgressSample>(parser.Feed("progress=end\r"));

        Assert.True(sample.Ended);
        Assert.Equal(TimeSpan.FromSeconds(1), sample.MediaTime);
    }
}
