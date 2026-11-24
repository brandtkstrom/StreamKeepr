using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Emotes;
using StreamKeepr.Mock;

namespace StreamKeepr.Tests.Mock;

public sealed class MockEmoteProviderTests
{
    [Fact]
    public async Task The_fixture_covers_static_punctuation_animated_and_failed_images()
    {
        MockEmoteProvider provider = CreateProvider();

        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        Assert.Equal("bttv", provider.Id);
        Assert.True(provider.Enabled);
        Assert.Contains(entries, entry => entry is { Code: "KEKW", Animated: false });
        Assert.Contains(entries, entry => entry is { Code: ":tf:", Animated: false });
        Assert.Contains(entries, entry => entry is { Code: "CatJAM", Animated: true });
        Assert.Contains(
            entries,
            entry => entry.Code == "BrokenEmote" && entry.Id == MockEmoteProvider.MissingImageId);
        Assert.Empty(await provider.GetChannelAsync("9100001", CancellationToken.None));
    }

    [Fact]
    public async Task Image_urls_use_the_mock_host_and_animated_entries_use_the_static_frame()
    {
        MockEmoteProvider provider = CreateProvider();
        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        Assert.All(entries, entry =>
        {
            EmoteImageSet image = provider.GetImageSet(entry);
            Assert.StartsWith(
                $"{MockEmoteProvider.ImageHost}/emote/{entry.Id}/", image.Src, StringComparison.Ordinal);
            Assert.DoesNotContain("betterttv", image.Src, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("betterttv", image.SrcSet, StringComparison.OrdinalIgnoreCase);
        });

        EmoteCatalogEntry animated = Assert.Single(entries, entry => entry.Animated);
        Assert.EndsWith("/static/1x.webp", provider.GetImageSet(animated).Src, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fixture_is_always_enabled()
    {
        // The Mock profile replaces BetterTTV entirely, so the production flag cannot disable it.
        Assert.True(CreateProvider().Enabled);
    }

    [Fact]
    public async Task Animated_entries_use_the_animated_asset_when_animation_is_enabled()
    {
        MockEmoteProvider provider = CreateProvider(animated: true);
        IReadOnlyList<EmoteCatalogEntry> entries =
            await provider.GetGlobalAsync(CancellationToken.None);

        EmoteCatalogEntry animated = Assert.Single(entries, entry => entry.Animated);
        Assert.EndsWith("/1x.webp", provider.GetImageSet(animated).Src, StringComparison.Ordinal);
        Assert.DoesNotContain("/static/", provider.GetImageSet(animated).Src, StringComparison.Ordinal);
    }

    private static MockEmoteProvider CreateProvider(bool animated = false) =>
        new(Options.Create(new ChatOptions
        {
            Emotes = new ChatEmoteOptions { Animated = animated },
        }));
}
