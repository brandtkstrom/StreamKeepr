using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Hosting;

/// <summary>
/// Serves the stitched playlist for a recording session. A finalized index is normally served by
/// the static media surface; this endpoint covers the live case (no file yet) and any finalized
/// request that reaches routing.
/// </summary>
public static class LiveIndexEndpoint
{
    public const string Route = "/media/{channel}/{id}/index.m3u8";

    public static async Task<IResult> HandleAsync(
        string channel,
        string id,
        HttpContext context,
        SessionStore store,
        IOptions<StorageOptions> storage,
        CancellationToken ct)
    {
        // Playlists change while recording; this applies to 404s as well.
        context.Response.Headers.CacheControl = "no-cache";

        if (!TrySessionDirectory(storage.Value.RecordingsRoot, channel, id, out string directory))
            return Results.NotFound();

        string? playlist = await store.StitchLiveAsync(directory, ct);
        if (playlist is null)
            return Results.NotFound();

        return Results.Text(playlist, RecordingsMedia.PlaylistContentType);
    }

    internal static bool TrySessionDirectory(
        string recordingsRoot, string channel, string id, out string directory)
    {
        directory = "";
        if (!AuthOptions.IsValidLogin(channel) || !Session.IsValidId(id))
            return false;

        try
        {
            string root = Path.GetFullPath(recordingsRoot);
            string prefix = Path.EndsInDirectorySeparator(root)
                ? root
                : root + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(root, channel, id));
            if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            directory = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }
}
