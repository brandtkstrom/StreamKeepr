using System.Collections.Concurrent;

namespace StreamKeepr.Infrastructure.Emotes;

/// <summary>
/// One third-party emote catalog source. Providers fetch, normalize, and assemble their own
/// static image URLs; the aggregation service owns caching, ordering, dedupe, and stale fallback.
/// </summary>
internal interface IEmoteProvider
{
    /// <summary>Stable provider identifier used in responses and logs (for example, `bttv`).</summary>
    string Id { get; }

    /// <summary>Whether this provider's configuration enables it for the current host.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Fetches and normalizes the provider's global catalog. Implementations throw
    /// <see cref="EmoteProviderException"/> on transport or payload failures.
    /// </summary>
    Task<IReadOnlyList<EmoteCatalogEntry>> GetGlobalAsync(CancellationToken ct);

    /// <summary>
    /// Fetches and normalizes one channel's catalog. `channelId` is a validated numeric Twitch
    /// user ID. A provider with no profile for the channel returns an empty catalog.
    /// </summary>
    Task<IReadOnlyList<EmoteCatalogEntry>> GetChannelAsync(string channelId, CancellationToken ct);

    /// <summary>Builds the static 1x/2x/3x image URLs for a validated entry.</summary>
    EmoteImageSet GetStaticImage(EmoteCatalogEntry entry);
}

/// <summary>
/// Resolves a channel login to the numeric Twitch user ID that providers key catalogs by.
/// The production implementation resolves from the stored channel list first and Helix second,
/// and its five-minute cache memoizes either result.
/// </summary>
internal interface IEmoteChannelResolver
{
    /// <summary>Returns the numeric user ID, or null when the login cannot be resolved.</summary>
    Task<string?> ResolveAsync(string login, CancellationToken ct);
}

internal sealed record EmoteCatalogEntry(string Id, string Code, bool Animated);

internal sealed record EmoteImageSet(string Src, string SrcSet);

/// <summary>
/// Builds the shared 1x/2x/3x static-image URL shape. Providers differ only by CDN host;
/// keeping one builder makes the URL contract single-sourced (SEC-003).
/// </summary>
internal static class StaticEmoteImage
{
    /// <param name="host">Absolute CDN origin without a trailing slash.</param>
    public static EmoteImageSet Create(string host, EmoteCatalogEntry entry)
    {
        string folder = entry.Animated ? "static/" : "";
        string Url(string size) => $"{host}/emote/{entry.Id}/{folder}{size}.webp";

        return new EmoteImageSet(
            Url("1x"), $"{Url("1x")} 1x, {Url("2x")} 2x, {Url("3x")} 3x");
    }
}

/// <summary>
/// Keeps a cache dictionary bounded: above <paramref name="limit"/> entries it first removes
/// expired slots, then arbitrary slots until the size is back under the limit. The second pass
/// is what bounds the cache when every entry is still fresh.
/// </summary>
internal static class BoundedEmoteCache
{
    public static void Trim<T>(
        ConcurrentDictionary<string, SingleFlightSlot<T>> cache,
        int limit,
        DateTimeOffset now,
        TimeSpan ttl,
        Func<T, DateTimeOffset> fetchedAt)
        where T : class
    {
        if (cache.Count <= limit)
            return;

        foreach (KeyValuePair<string, SingleFlightSlot<T>> entry in cache)
        {
            if (entry.Value.Value is not { } value || now - fetchedAt(value) >= ttl)
                cache.TryRemove(entry.Key, out _);
        }

        foreach (string key in cache.Keys)
        {
            if (cache.Count <= limit)
                break;

            cache.TryRemove(key, out _);
        }
    }
}

/// <summary>Why a provider refresh failed, so diagnostics never report a fake success status.</summary>
internal enum EmoteProviderFailureKind
{
    HttpStatus,
    InvalidPayload,
    TooLarge,
}

/// <summary>
/// One cached value with a single-flight gate. The gate serialises refreshes for one cache key;
/// callers check <see cref="Value"/> before waiting and again after acquiring the gate.
/// </summary>
internal sealed class SingleFlightSlot<T> where T : class
{
    private T? _value;

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public T? Value
    {
        get => Volatile.Read(ref _value);
        set => Volatile.Write(ref _value, value);
    }
}

/// <summary>A provider failure that carries enough context for the service's single warning.</summary>
internal sealed class EmoteProviderException(
    string providerId,
    int? httpStatus,
    string message,
    EmoteProviderFailureKind kind = EmoteProviderFailureKind.HttpStatus) : Exception(message)
{
    public string ProviderId { get; } = providerId;

    /// <summary>The HTTP status, or null when the failure is not an HTTP response.</summary>
    public int? HttpStatus { get; } = httpStatus;

    public EmoteProviderFailureKind Kind { get; } = kind;
}
