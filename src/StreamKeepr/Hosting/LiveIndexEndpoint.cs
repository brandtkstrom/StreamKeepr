using System.Text;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Hosting;

/// <summary>
/// Serves the playlist for a recording session. A finalized index is returned as a
/// physical file so HEAD and Range keep their static-file semantics; a recording
/// session returns the stitched live playlist without ENDLIST.
/// </summary>
public static class LiveIndexEndpoint
{
    public const string Route = "/media/{channel}/{id}/index.m3u8";

    public static async Task<IResult> HandleAsync(
        string channel,
        string id,
        HttpContext context,
        SessionStore store,
        RecordingLibrary library,
        IOptions<StorageOptions> storage,
        CancellationToken ct)
    {
        // Playlists change while recording; this applies to 404s as well.
        context.Response.Headers.CacheControl = "no-cache";

        // Once recovery has loaded the library, it is the authority for deletes:
        // delete removes the entry before touching the files. Before that scan runs,
        // fall back to the on-disk manifest so the endpoint still works.
        if (library.IsLoaded
            && (library.Find(id) is not { } known
                || !string.Equals(known.Channel, channel, StringComparison.Ordinal)))
        {
            return Results.NotFound();
        }

        if (!TrySessionDirectory(storage.Value.RecordingsRoot, channel, id, out string directory))
            return Results.NotFound();

        var manifest = await store.TryLoadAsync(directory, ct);
        if (manifest is null)
            return Results.NotFound();

        if (manifest.Status == SessionStatus.Complete)
        {
            string indexPath = Path.Combine(directory, SessionStore.IndexFileName);
            return File.Exists(indexPath)
                ? Results.File(
                    indexPath, RecordingsMedia.PlaylistContentType, enableRangeProcessing: true)
                : Results.NotFound();
        }

        string? playlist = await store.StitchLiveAsync(directory, ct);
        if (playlist is null)
            return Results.NotFound();

        if (HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.ContentType = RecordingsMedia.PlaylistContentType;
            context.Response.ContentLength = Encoding.UTF8.GetByteCount(playlist);
            return Results.Empty;
        }

        return Results.Text(playlist, RecordingsMedia.PlaylistContentType);
    }

    private static bool TrySessionDirectory(
        string recordingsRoot, string channel, string id, out string directory)
    {
        directory = "";
        return AuthOptions.IsValidLogin(channel)
            && Session.IsValidId(id)
            && RecordingsPath.TryResolve(recordingsRoot, [channel, id], out directory);
    }
}
