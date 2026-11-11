using System.Text.Json;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Hosting;

/// <summary>
/// Public <c>PUT /api/recordings/{id}/progress</c>, used by the player's keepalive flush on
/// page hide and route change. Viewing is open, so it needs no sign-in or antiforgery token.
/// </summary>
public static class ProgressEndpoint
{
    public const string Route = "/api/recordings/{id}/progress";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<IResult> HandleAsync(
        string id,
        HttpContext context,
        RecordingLibrary library,
        IProgressStore progress,
        CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";

        // The body is read here rather than bound so every rejection carries no-store and
        // reads the same way: missing, malformed, non-finite, or negative is a 400.
        ProgressRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ProgressRequest>(
                context.Request.Body, Json, ct);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        if (request is null || !double.IsFinite(request.PositionSec) || request.PositionSec < 0)
            return Results.BadRequest();

        if (library.Find(id) is null)
            return Results.NotFound();

        await progress.SetAsync(id, request.PositionSec, ct);
        return Results.NoContent();
    }
}
