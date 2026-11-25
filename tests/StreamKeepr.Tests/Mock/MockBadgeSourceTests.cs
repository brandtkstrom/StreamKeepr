using StreamKeepr.Infrastructure.Badges;
using StreamKeepr.Mock;

namespace StreamKeepr.Tests.Mock;

public sealed class MockBadgeSourceTests
{
    [Fact]
    public async Task The_fixture_covers_the_badges_the_mock_chat_uses()
    {
        var source = new MockBadgeSource();

        IReadOnlyList<BadgeCatalogEntry> entries =
            await source.GetGlobalAsync(CancellationToken.None);

        Assert.Contains(entries, entry => entry is { Set: "moderator", Version: "1" });
        Assert.Contains(entries, entry => entry is { Set: "vip", Version: "1" });
        Assert.Contains(entries, entry => entry is { Set: "broadcaster", Version: "1" });
        Assert.Contains(entries, entry => entry is { Set: "subscriber", Version: "0" });
        Assert.Contains(entries, entry => entry is { Set: "subscriber", Version: "12" });
    }

    [Fact]
    public async Task The_first_channel_overrides_subscriber_and_adds_channel_badges()
    {
        IReadOnlyList<BadgeCatalogEntry> entries =
            await new MockBadgeSource().GetChannelAsync(
                MockBadgeSource.ChannelBroadcasterId, CancellationToken.None);

        Assert.Equal(
            ["subscriber/0", "subscriber/3", "bits/100"],
            entries.Select(entry => $"{entry.Set}/{entry.Version}"));
        Assert.All(entries, entry =>
        {
            Assert.StartsWith(
                $"{MockBadgeSource.ImageHost}/channel/", entry.Src, StringComparison.Ordinal);
            Assert.Contains(entry.Src, entry.SrcSet, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Other_channels_have_no_custom_catalog()
    {
        IReadOnlyList<BadgeCatalogEntry> entries =
            await new MockBadgeSource().GetChannelAsync("9100003", CancellationToken.None);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task Image_urls_use_the_reserved_mock_host()
    {
        var source = new MockBadgeSource();
        IReadOnlyList<BadgeCatalogEntry> entries =
        [
            .. await source.GetGlobalAsync(CancellationToken.None),
            .. await source.GetChannelAsync(
                MockBadgeSource.ChannelBroadcasterId, CancellationToken.None),
        ];

        Assert.All(entries, entry =>
        {
            Assert.StartsWith(MockBadgeSource.ImageHost, entry.Src, StringComparison.Ordinal);
            Assert.Contains(entry.Src, entry.SrcSet, StringComparison.Ordinal);
            Assert.DoesNotContain("jtvnw", entry.Src, StringComparison.OrdinalIgnoreCase);
        });
    }
}
