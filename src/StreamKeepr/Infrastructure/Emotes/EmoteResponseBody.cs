using StreamKeepr.Infrastructure.Http;

namespace StreamKeepr.Infrastructure.Emotes;

/// <summary>
/// Reads a provider response body while enforcing the shared size cap. Provider payloads are
/// untrusted input, so the declared length and the streamed bytes are both checked before the
/// body is buffered.
/// </summary>
internal static class EmoteResponseBody
{
    /// <summary>
    /// Buffers a provider body, rejecting one that exceeds <see cref="EmoteCatalogRules.MaxResponseBytes"/>
    /// by its declared length or by the bytes actually read.
    /// </summary>
    public static Task<byte[]> ReadAsync(
        HttpResponseMessage response,
        string providerId,
        string providerName,
        CancellationToken ct) =>
        ResponseBodyReader.ReadAsync(
            response,
            EmoteCatalogRules.MaxResponseBytes,
            () => EmoteProviderException.ResponseTooLarge(providerId, providerName),
            ct);
}
