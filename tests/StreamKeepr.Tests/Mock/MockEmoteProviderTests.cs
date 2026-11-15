using StreamKeepr.Infrastructure.Emotes;
using StreamKeepr.Mock;

namespace StreamKeepr.Tests.Mock;

public sealed class MockEmoteProviderTests
{
    [Fact]
    public async Task The_fixture_covers_static_punctuation_animated_and_failed_images()
    {
        var provider = new MockEmoteProvider();

        EmoteProviderCatalog catalog = await provider.GetCatalogAsync(
            new EmoteChannel("mock_nova", "9100001"), CancellationToken.None);

        Assert.Equal("bttv", provider.Id);
        Assert.Empty(catalog.Channel);
        Assert.Contains(catalog.Global, entry => entry is { Code: "KEKW", Animated: false });
        Assert.Contains(catalog.Global, entry => entry is { Code: ":tf:", Animated: false });
        Assert.Contains(catalog.Global, entry => entry is { Code: "CatJAM", Animated: true });
        Assert.Contains(
            catalog.Global,
            entry => entry.Code == "BrokenEmote" && entry.Id == MockEmoteProvider.MissingImageId);
    }

    [Fact]
    public async Task Image_urls_use_the_mock_host_and_animated_entries_use_the_static_frame()
    {
        var provider = new MockEmoteProvider();
        EmoteProviderCatalog catalog = await provider.GetCatalogAsync(
            new EmoteChannel("mock_nova", "9100001"), CancellationToken.None);

        Assert.All(catalog.Global, entry =>
        {
            EmoteImageSet image = provider.GetStaticImage(entry);
            Assert.StartsWith(
                $"{MockEmoteProvider.ImageHost}/emote/{entry.Id}/", image.Src, StringComparison.Ordinal);
            Assert.DoesNotContain("betterttv", image.Src, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("betterttv", image.SrcSet, StringComparison.OrdinalIgnoreCase);
        });

        EmoteCatalogEntry animated = Assert.Single(catalog.Global, entry => entry.Animated);
        Assert.EndsWith("/static/1x.webp", provider.GetStaticImage(animated).Src, StringComparison.Ordinal);
    }
}
