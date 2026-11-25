using System.Globalization;
using System.Text.Json.Serialization;
using StreamKeepr.Infrastructure.Badges;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Hosting;

/// <summary>
/// Public read-only global badge catalog for a recording. Optional decoration: a failed fetch
/// serves the stale cache or an empty, unavailable catalog with a 200, so it never blocks chat.
/// <c>channel</c> is present for the channel-scope extension (#277) and is always empty here.
/// </summary>
internal static class BadgeEndpoint
{
    public const string Route = "/api/recordings/{id}/badges";

    public static async Task<IResult> HandleAsync(
        string id,
        HttpContext context,
        RecordingLibrary library,
        BadgeCatalogService catalog,
        CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";

        if (library.Find(id) is null)
            return Results.NotFound();

        BadgeCatalogResult result = await catalog.GetGlobalAsync(ct);
        return Results.Json(BadgeCatalogResponse.From(result));
    }
}

internal sealed record BadgeCatalogResponse(
    int V,
    bool Available,
    bool Stale,
    string? FetchedAt,
    IReadOnlyList<BadgeCatalogEntryResponse> Global,
    IReadOnlyList<BadgeCatalogEntryResponse> Channel)
{
    public const int CurrentVersion = 1;

    public static BadgeCatalogResponse From(BadgeCatalogResult result)
    {
        var global = new BadgeCatalogEntryResponse[result.Global.Count];
        for (int index = 0; index < global.Length; index++)
        {
            BadgeCatalogEntry entry = result.Global[index];
            global[index] = new BadgeCatalogEntryResponse(
                entry.Set,
                entry.Version,
                entry.Title,
                entry.Src,
                entry.SrcSet);
        }

        return new BadgeCatalogResponse(
            CurrentVersion,
            result.Available,
            result.Stale,
            result.FetchedAt?.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            global,
            []);
    }
}

internal sealed record BadgeCatalogEntryResponse(
    string Set,
    string Version,
    string Title,
    string Src,
    [property: JsonPropertyName("srcset")] string SrcSet);
