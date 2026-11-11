using StreamKeepr.Infrastructure.Media;

namespace StreamKeepr.Tests.Media;

public sealed class StreamlinkAdBreakTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

    [Fact]
    public void An_announcement_grants_the_break_length_plus_the_safety_margin()
    {
        bool parsed = StreamlinkAdBreak.TryGetGraceUntil(
            "[cli][info] Detected advertisement break of 90 seconds",
            Now,
            out DateTimeOffset graceUntil);

        Assert.True(parsed);
        Assert.Equal(Now + TimeSpan.FromSeconds(90) + TimeSpan.FromSeconds(30), graceUntil);
    }

    [Fact]
    public void A_singular_second_is_accepted()
    {
        bool parsed = StreamlinkAdBreak.TryGetGraceUntil(
            "Detected advertisement break of 1 second",
            Now,
            out DateTimeOffset graceUntil);

        Assert.True(parsed);
        Assert.Equal(Now + TimeSpan.FromSeconds(31), graceUntil);
    }

    [Theory]
    [InlineData("stream ended")]
    [InlineData("Detected advertisement break of many seconds")]
    [InlineData("Detected advertisement break")]
    [InlineData("")]
    public void Other_lines_do_not_grant_grace(string line)
    {
        Assert.False(StreamlinkAdBreak.TryGetGraceUntil(line, Now, out _));
    }
}
