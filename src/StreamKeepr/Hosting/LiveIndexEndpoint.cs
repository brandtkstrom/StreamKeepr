using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
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

    public static Task<IResult> HandleAsync(
        string channel,
        string id,
        HttpContext context,
        RecordingLibrary library,
        LivePlaylistCache cache,
        IOptions<StorageOptions> storage)
    {
        // Playlists change while recording; this applies to 404s and 503s as well.
        context.Response.Headers.CacheControl = "no-cache";

        // Recovery marks the library loaded before the host accepts requests. Before that,
        // nothing may fall back to a disk scan.
        if (!library.IsLoaded)
            return Task.FromResult(Unavailable(context));

        RecordingSummary? summary = library.Find(id);
        if (summary is null
            || !string.Equals(summary.Channel, channel, StringComparison.Ordinal))
        {
            return Task.FromResult<IResult>(Results.NotFound());
        }

        if (!summary.IsRecording)
        {
            if (!RecordingsPath.TryResolve(
                    storage.Value.RecordingsRoot,
                    [summary.Channel, summary.Id],
                    out string directory))
            {
                return Task.FromResult<IResult>(Results.NotFound());
            }

            string indexPath = Path.Combine(directory, SessionStore.IndexFileName);
            return Task.FromResult<IResult>(
                File.Exists(indexPath)
                    ? Results.File(
                        indexPath, RecordingsMedia.PlaylistContentType, enableRangeProcessing: true)
                    : Results.NotFound());
        }

        LivePlaylistSnapshot? snapshot = cache.Find(summary.Id);
        if (snapshot is null)
            return Task.FromResult(Unavailable(context));

        return Task.FromResult<IResult>(new LivePlaylistResult(snapshot));
    }

    private static IResult Unavailable(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "1";
        return Results.Empty;
    }
}

/// <summary>
/// Serves one immutable snapshot without re-encoding, re-stitching, or touching the session
/// folder. A write that races the client disconnect is not an error.
/// </summary>
internal sealed class LivePlaylistResult(LivePlaylistSnapshot snapshot) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.ContentType = RecordingsMedia.PlaylistContentType;
        context.Response.ContentLength = snapshot.Utf8.Length;

        if (HttpMethods.IsHead(context.Request.Method))
            return;

        try
        {
            await context.Response.Body.WriteAsync(snapshot.Utf8, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away after the headers; Kestrel records the abort.
        }
        catch (IOException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Same abort surfaced through the response stream.
        }
    }
}
