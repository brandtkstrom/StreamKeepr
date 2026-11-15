namespace StreamKeepr.Infrastructure.Emotes;

/// <summary>
/// One third-party emote catalog source. Providers only fetch and normalize; the aggregation
/// service owns caching, ordering, dedupe, and image URL assembly.
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

internal sealed record EmoteChannel(string Login, string? TwitchUserId);

internal sealed record EmoteCatalogEntry(string Id, string Code, bool Animated);

internal sealed record EmoteImageSet(string Src, string SrcSet);

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
