namespace StreamKeepr.Infrastructure.Emotes;

/// <summary>
/// One third-party emote catalog source. Providers only fetch and normalize; the aggregation
/// service owns caching, ordering, dedupe, and image URL assembly.
/// </summary>
internal interface IEmoteProvider
{
    /// <summary>Stable provider identifier used in responses and logs (for example, `bttv`).</summary>
    string Id { get; }

    /// <summary>
    /// Fetches and normalizes the provider catalogs for a channel. Implementations throw
    /// <see cref="EmoteProviderException"/> on transport or payload failures.
    /// </summary>
    Task<EmoteProviderCatalog> GetCatalogAsync(EmoteChannel channel, CancellationToken ct);

    /// <summary>Builds the static 1x/2x/3x image URLs for a validated entry.</summary>
    EmoteImageSet GetStaticImage(EmoteCatalogEntry entry);
}

internal sealed record EmoteChannel(string Login, string? TwitchUserId);

/// <summary>The channel catalog is ordered by the provider; shared entries precede channel entries.</summary>
internal sealed record EmoteProviderCatalog(
    IReadOnlyList<EmoteCatalogEntry> Channel,
    IReadOnlyList<EmoteCatalogEntry> Global);

internal sealed record EmoteCatalogEntry(string Id, string Code, bool Animated, bool Modifier);

internal sealed record EmoteImageSet(string Src, string SrcSet);

/// <summary>A provider failure that carries enough context for the service's single warning.</summary>
internal sealed class EmoteProviderException(
    string providerId, int? httpStatus, string message) : Exception(message)
{
    public string ProviderId { get; } = providerId;

    public int? HttpStatus { get; } = httpStatus;
}
