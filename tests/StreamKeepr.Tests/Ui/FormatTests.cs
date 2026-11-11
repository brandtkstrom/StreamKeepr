using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class FormatTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(59, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3733, "1:02:13")]
    public void Timecode_formats_hours_only_when_needed(double seconds, string expected) =>
        Assert.Equal(expected, Format.Timecode(seconds));

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(45, "45s")]
    [InlineData(60, "1m")]
    [InlineData(120, "2m")]
    [InlineData(8047, "2h 14m")]
    [InlineData(14575, "4h 02m")]
    [InlineData(3600, "1h 00m")]
    public void Duration_formats_hours_and_minutes(double seconds, string expected) =>
        Assert.Equal(expected, Format.Duration(seconds));

    [Theory]
    [InlineData(null, "—")]
    [InlineData(-1L, "—")]
    [InlineData(0L, "0 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(9_800_000_000L, "9.1 GB")]
    [InlineData(10_522_624_000L, "9.8 GB")]
    [InlineData(1_319_413_760_000L, "1.2 TB")]
    public void Bytes_formats_binary_units(long? bytes, string expected) =>
        Assert.Equal(expected, Format.Bytes(bytes));

    [Fact]
    public void DayHeader_uses_today_yesterday_then_the_date()
    {
        var zone = TimeZoneInfo.Utc;
        var now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("Today", Format.DayHeader(now.AddHours(-2), now, zone));
        Assert.Equal("Yesterday", Format.DayHeader(now.AddDays(-1), now, zone));
        Assert.Equal("Wed, Nov 4", Format.DayHeader(new DateTimeOffset(2026, 11, 4, 18, 0, 0, TimeSpan.Zero), now, zone));
    }

    [Fact]
    public void DayHeader_handles_a_dst_boundary_in_the_viewer_zone()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var now = new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero); // Mar 8 08:00 EDT

        Assert.Equal("Today", Format.DayHeader(new DateTimeOffset(2026, 3, 8, 6, 59, 0, TimeSpan.Zero), now, zone));
        Assert.Equal("Yesterday", Format.DayHeader(new DateTimeOffset(2026, 3, 7, 6, 0, 0, TimeSpan.Zero), now, zone));
    }
}
