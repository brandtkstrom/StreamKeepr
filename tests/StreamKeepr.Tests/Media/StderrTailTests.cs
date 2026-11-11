using StreamKeepr.Infrastructure.Media;

namespace StreamKeepr.Tests.Media;

public sealed class StderrTailTests
{
    [Fact]
    public void Keeps_the_last_lines_in_order()
    {
        var tail = new StderrTail(capacity: 2);

        tail.Add("streamlink", "first");
        tail.Add("ffmpeg", "second");
        tail.Add("ffmpeg", "third");

        Assert.Equal("ffmpeg: second | ffmpeg: third", tail.ToString());
    }

    [Fact]
    public void Rejects_a_non_positive_capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StderrTail(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StderrTail(-1));
    }
}
