using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Hosting;

/// <summary>
/// Public read-only viewer-count samples for the Stats viewer chart (player contract §10). Like
/// chat and media, viewing is open; the private-home-use scope is the exposure boundary.
/// </summary>
public static class ViewersEndpoint
{
    public const string Route = "/api/recordings/{id}/viewers";

    public static async Task<IResult> HandleAsync(
        string id,
        HttpContext context,
        RecordingLibrary library,
        IOptions<StorageOptions> storage,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        // Samples grow while live; this applies to 404s as well.
        context.Response.Headers.CacheControl = "no-store";

        if (library.Find(id) is not { } summary
            || !RecordingsPath.TryResolveSession(
                storage.Value.RecordingsRoot, summary.Channel, summary.Id, out string directory))
        {
            return Results.NotFound();
        }

        IReadOnlyList<ViewerSample> samples = await ViewerSamples.ReadAsync(
            directory, loggers.CreateLogger(typeof(ViewersEndpoint).FullName!), ct);
        double duration = ViewerSamples.RoundSeconds(summary.DurationAt(clock.GetUtcNow()));
        return Results.Json(new ViewersResponse(ViewerSamples.Version, summary.Id, duration, samples));
    }
}

/// <summary>The <c>GET /api/recordings/{id}/viewers</c> body.</summary>
public sealed record ViewersResponse(
    int V, string RecordingId, double DurationSec, IReadOnlyList<ViewerSample> Samples);
