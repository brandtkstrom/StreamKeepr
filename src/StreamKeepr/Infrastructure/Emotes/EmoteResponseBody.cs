namespace StreamKeepr.Infrastructure.Emotes;

/// <summary>
/// Reads a provider response body while enforcing the shared size cap. Provider payloads are
/// untrusted input, so the declared length and the streamed bytes are both checked before the
/// body is buffered.
/// </summary>
internal static class EmoteResponseBody
{
    public static async Task<byte[]> ReadAsync(
        HttpResponseMessage response,
        Func<EmoteProviderException> tooLarge,
        CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > EmoteCatalogRules.MaxResponseBytes)
            throw tooLarge();

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > EmoteCatalogRules.MaxResponseBytes)
                throw tooLarge();

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
