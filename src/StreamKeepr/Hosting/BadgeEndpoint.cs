using System.Globalization;
using System.Text.Json.Serialization;
using StreamKeepr.Infrastructure.Badges;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Hosting;

/// <summary>
/// Public read-only global and channel badge catalogs for a recording. Optional decoration: a
/// failed scope serves its stale cache or an empty list with a 200, so it never blocks chat. The
/// panes merge the scopes by exact set and version, with the channel taking precedence.
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

        if (library.Find(id) is not { } summary)
            return Results.NotFound();

        BadgeCatalogResult result = await catalog.GetCatalogAsync(summary.Channel, ct);
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
        return new BadgeCatalogResponse(
            CurrentVersion,
            result.Available,
            result.Stale,
            result.FetchedAt?.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Entries(result.Global),
            Entries(result.Channel));
    }

    private static BadgeCatalogEntryResponse[] Entries(
        IReadOnlyList<BadgeCatalogEntry> entries)
    {
        var response = new BadgeCatalogEntryResponse[entries.Count];
        for (int index = 0; index < response.Length; index++)
        {
            BadgeCatalogEntry entry = entries[index];
            response[index] = new BadgeCatalogEntryResponse(
                entry.Set,
                entry.Version,
                entry.Title,
                entry.Src,
                entry.SrcSet);
        }

        return response;
    }
}

internal sealed record BadgeCatalogEntryResponse(
    string Set,
    string Version,
    string Title,
    string Src,
    [property: JsonPropertyName("srcset")] string SrcSet);
