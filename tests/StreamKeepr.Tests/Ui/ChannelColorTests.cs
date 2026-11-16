using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class ChannelColorTests
{
    [Theory]
    // Pinned values (checked against an independent FNV-1a): changing the hash would recolour every channel the viewer already knows.
    [InlineData("pixelwren", 6)]
    [InlineData("mock_rain", 3)]
    [InlineData("mock_nova", 3)]
    [InlineData("a", 5)]
    public void A_login_always_maps_to_the_same_colour(string login, int expected)
    {
        Assert.Equal(expected, ChannelColor.For(login));
    }

    [Fact]
    public void The_colour_belongs_to_the_login_whatever_its_case_or_padding()
    {
        Assert.Equal(ChannelColor.For("pixelwren"), ChannelColor.For("PixelWren"));
        Assert.Equal(ChannelColor.For("pixelwren"), ChannelColor.For(" pixelwren "));
    }

    [Fact]
    public void Logins_spread_over_all_eight_colours()
    {
        HashSet<int> seen = [];
        for (int index = 0; index < 200; index++)
            seen.Add(ChannelColor.For($"channel_{index}"));

        Assert.Equal(Enumerable.Range(1, ChannelColor.Count), seen.Order());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_login_is_rejected(string? login) =>
        Assert.ThrowsAny<ArgumentException>(() => ChannelColor.For(login!));
}
