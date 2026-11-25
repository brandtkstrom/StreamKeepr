namespace StreamKeepr.Infrastructure.Badges;

/// <summary>One badge image a catalog publishes for a set and version.</summary>
internal sealed record BadgeCatalogEntry(
    string Set, string Version, string Title, string Src, string SrcSet);

/// <summary>
/// One badge catalog source. Badges are not emotes, so this seam is deliberately separate from
/// <c>IEmoteProvider</c>: the catalogs have different scopes, payloads, and lifecycles. A source
/// fetches and normalizes; the service owns caching, single-flight, and stale fallback.
/// </summary>
internal interface IBadgeSource
{
    /// <summary>
    /// Fetches the global badge catalog. A successful empty catalog is available. Implementations
    /// throw <see cref="BadgeCatalogException"/> on transport or payload failures.
    /// </summary>
    Task<IReadOnlyList<BadgeCatalogEntry>> GetGlobalAsync(CancellationToken ct);
}

/// <summary>Validation and size limits for an untrusted badge catalog payload.</summary>
internal static class BadgeCatalogRules
{
    /// <summary>
    /// The response is bounded before parsing, like an emote catalog. Twitch's published global
    /// catalog is far smaller, so this is a defensive ceiling rather than a measured expectation.
    /// </summary>
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    public const int MaxSets = 500;
    public const int MaxVersionsPerSet = 100;
    public const int MaxIdLength = 64;
    public const int MaxTitleLength = 128;

    /// <summary>Badge images are served from Twitch's static CDN only.</summary>
    public const string ImageHost = "static-cdn.jtvnw.net";

    /// <summary>Set and version IDs are single URL-safe path segments: <c>[a-z0-9_-]{1,64}</c>.</summary>
    public static bool IsValidId(string? value) =>
        value is { Length: >= 1 and <= MaxIdLength } &&
        value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');

    /// <summary>A badge image is an absolute HTTPS URL on the one allowed host.</summary>
    public static bool IsValidImageUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        string.Equals(uri.Host, ImageHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the 1x/2x/4x candidate list from the URLs Twitch supplied, or null when any of them
    /// is not a valid badge image. Twitch's 4x URL ends in <c>/3</c>; it is used verbatim.
    /// </summary>
    public static string? BuildSrcSet(string? oneX, string? twoX, string? fourX) =>
        IsValidImageUrl(oneX) && IsValidImageUrl(twoX) && IsValidImageUrl(fourX)
            ? $"{oneX} 1x, {twoX} 2x, {fourX} 4x"
            : null;

    /// <summary>The catalog title, falling back to the set name and capped at the title limit.</summary>
    public static string NormalizeTitle(string? title, string set)
    {
        string value = string.IsNullOrWhiteSpace(title) ? set : title.Trim();
        return value.Length <= MaxTitleLength ? value : value[..MaxTitleLength];
    }
}

/// <summary>Why a badge catalog refresh failed, so diagnostics never report a fake success status.</summary>
internal enum BadgeCatalogFailureKind
{
    HttpStatus,
    InvalidPayload,
    TooLarge,
}

/// <summary>A badge source failure with the context the service's one warning needs.</summary>
internal sealed class BadgeCatalogException(
    int? httpStatus,
    string message,
    BadgeCatalogFailureKind kind = BadgeCatalogFailureKind.HttpStatus) : Exception(message)
{
    /// <summary>The HTTP status, or null when the failure is not an HTTP response.</summary>
    public int? HttpStatus { get; } = httpStatus;

    public BadgeCatalogFailureKind Kind { get; } = kind;

    public static BadgeCatalogException HttpFailure(int status) =>
        new(status, $"The Twitch badge catalog request failed with HTTP {status}.");

    public static BadgeCatalogException InvalidPayload() =>
        new(null, "Twitch returned an invalid global badge catalog.",
            BadgeCatalogFailureKind.InvalidPayload);

    public static BadgeCatalogException ResponseTooLarge() =>
        new(null, "The Twitch badge catalog response exceeded the response size limit.",
            BadgeCatalogFailureKind.TooLarge);
}
