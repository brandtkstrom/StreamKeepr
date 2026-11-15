using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Emotes;

namespace StreamKeepr.Mock;

/// <summary>
/// The Mock profile's fixed third-party catalog. It replaces the BetterTTV provider so local and
/// E2E runs never call <c>api.betterttv.net</c>, and its image URLs use the reserved
/// <c>.invalid</c> host so the browser never calls <c>cdn.betterttv.net</c> either. The E2E
/// journey intercepts that host and serves the image bytes itself.
/// </summary>
internal sealed class MockEmoteProvider(IOptions<ChatOptions> options) : IEmoteProvider
{
    public const string ImageHost = "https://emotes.mock.invalid";

    /// <summary>The deliberately missing image that exercises the text fallback.</summary>
    public const string MissingImageId = "000000000000000000000001";

    private static readonly EmoteCatalogEntry[] Fixture =
    [
        new("5ff36d0a95e8e11f4f6f0a1c", "KEKW", Animated: false),
        new("54fa8f1401e468494b85b537", ":tf:", Animated: false),
        new("566ca38765dbbdab32ec0560", "CatJAM", Animated: true),
        new(MissingImageId, "BrokenEmote", Animated: false),
    ];

    public string Id => BttvEmoteProvider.ProviderId;

    public bool Enabled => options.Value.Emotes.Bttv.Enabled;

    public Task<IReadOnlyList<EmoteCatalogEntry>> GetGlobalAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EmoteCatalogEntry>>(Fixture);

    public Task<IReadOnlyList<EmoteCatalogEntry>> GetChannelAsync(
        string channelId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EmoteCatalogEntry>>([]);

    /// <summary>
    /// Mirrors <see cref="BttvEmoteProvider.GetStaticImage"/> on the mock host: animated entries
    /// point at their still frame and static entries at the regular assets.
    /// </summary>
    public EmoteImageSet GetStaticImage(EmoteCatalogEntry entry)
    {
        string folder = entry.Animated ? "static/" : "";
        string Url(string size) => $"{ImageHost}/emote/{entry.Id}/{folder}{size}.webp";

        return new EmoteImageSet(
            Url("1x"), $"{Url("1x")} 1x, {Url("2x")} 2x, {Url("3x")} 3x");
    }
}
