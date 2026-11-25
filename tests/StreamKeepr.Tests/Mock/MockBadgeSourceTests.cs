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
    }

    [Fact]
    public async Task Image_urls_use_the_reserved_mock_host()
    {
        IReadOnlyList<BadgeCatalogEntry> entries =
            await new MockBadgeSource().GetGlobalAsync(CancellationToken.None);

        Assert.All(entries, entry =>
        {
            Assert.StartsWith(MockBadgeSource.ImageHost, entry.Src, StringComparison.Ordinal);
            Assert.Contains(entry.Src, entry.SrcSet, StringComparison.Ordinal);
            Assert.DoesNotContain("jtvnw", entry.Src, StringComparison.OrdinalIgnoreCase);
        });
    }
}
