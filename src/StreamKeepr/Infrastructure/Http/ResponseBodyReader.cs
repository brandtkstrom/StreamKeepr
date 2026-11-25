namespace StreamKeepr.Infrastructure.Http;

/// <summary>
/// Buffers an untrusted response body while enforcing a byte cap on both the declared length and
/// the bytes actually read. Catalog sources share this because the cap is the same rule for each
/// external body; only the domain-specific failure they throw differs.
/// </summary>
internal static class ResponseBodyReader
{
    private const int ChunkBytes = 81_920;

    public static async Task<byte[]> ReadAsync(
        HttpResponseMessage response,
        int maxBytes,
        Func<Exception> tooLarge,
        CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > maxBytes)
            throw tooLarge();

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[ChunkBytes];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw tooLarge();

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
