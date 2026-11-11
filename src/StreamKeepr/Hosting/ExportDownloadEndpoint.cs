using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Hosting;

/// <summary>
/// <c>GET /api/exports/{id}/download</c> for signed-in admins: the MP4 as an attachment with
/// range support; 404 when unknown or cleaned up; 409 while the job has not succeeded.
/// </summary>
public static class ExportDownloadEndpoint
{
    public const string Route = "/api/exports/{id}/download";

    public static string For(string exportId) => $"/api/exports/{Uri.EscapeDataString(exportId)}/download";

    public static IResult Handle(string id, HttpContext context, IExportService exports)
    {
        context.Response.Headers.CacheControl = "no-store";

        if (exports.Find(id) is not { } job)
            return Results.NotFound();
        if (job.State != ExportState.Succeeded)
            return Results.Conflict();
        if (exports.FilePath(id) is not { } path)
            return Results.NotFound();

        return Results.File(
            path,
            ExportService.ContentType,
            fileDownloadName: job.FileName ?? $"{job.Id}.mp4",
            enableRangeProcessing: true);
    }
}
