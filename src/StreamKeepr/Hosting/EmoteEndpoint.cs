using System.Globalization;
using System.Text.Json.Serialization;
using StreamKeepr.Infrastructure.Emotes;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Hosting;

/// <summary>
/// Public read-only third-party emote catalog for a recording's channel. Optional decoration:
/// a failed scope serves its stale cache or leaves only that scope out, and catalog failures
/// never affect chat or health. The response is unavailable only when no enabled provider has
/// a global or channel catalog.
/// </summary>
internal static class EmoteEndpoint
{
    public const string Route = "/api/recordings/{id}/emotes";

    public static async Task<IResult> HandleAsync(
        string id,
        HttpContext context,
        RecordingLibrary library,
        EmoteCatalogService catalog,
        CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";

        if (library.Find(id) is not { } summary)
            return Results.NotFound();

        EmoteCatalogResult result = await catalog.GetCatalogAsync(summary.Channel, ct);
        return Results.Json(EmoteCatalogResponse.From(result));
    }
}

internal sealed record EmoteCatalogResponse(
    int V,
    bool Available,
    bool Stale,
    string? FetchedAt,
    IReadOnlyList<EmoteCatalogResponseEntry> Entries)
{
    public const int CurrentVersion = 1;

    public static EmoteCatalogResponse From(EmoteCatalogResult result)
    {
        var entries = new EmoteCatalogResponseEntry[result.Entries.Count];
        for (int index = 0; index < entries.Length; index++)
        {
            EmoteCatalogItem item = result.Entries[index];
            entries[index] = new EmoteCatalogResponseEntry(
                item.Provider,
                item.Entry.Id,
                item.Entry.Code,
                item.Entry.Animated,
                item.Image.Src,
                item.Image.SrcSet);
        }

        return new EmoteCatalogResponse(
            CurrentVersion,
            result.Available,
            result.Stale,
            result.FetchedAt?.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            entries);
    }
}

internal sealed record EmoteCatalogResponseEntry(
    string Provider,
    string Id,
    string Code,
    bool Animated,
    string Src,
    [property: JsonPropertyName("srcset")] string SrcSet);
