using Microsoft.Extensions.Logging;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Hosting;

/// <summary>
/// Serves the playlist for a recording session. <see cref="LiveIndex.Lookup"/> decides what to
/// serve; this endpoint only maps that decision to HTTP. A session index is returned as a
/// physical file so HEAD and Range keep their static-file semantics; a recording session
/// returns its published live index without ENDLIST.
/// </summary>
public static class LiveIndexEndpoint
{
    public const string Route = "/media/{channel}/{id}/index.m3u8";

    /// <summary>Marks a 200 body that is last-known-good rather than freshly refreshed.</summary>
    public const string StaleHeaderName = "X-StreamKeepr-Stale";

    private const string LoggerCategory = "StreamKeepr.Hosting.LiveIndexEndpoint";

    public static Task<IResult> HandleAsync(
        string channel,
        string id,
        HttpContext context,
        LiveIndex liveIndex,
        ILoggerFactory loggers)
    {
        ILogger logger = loggers.CreateLogger(LoggerCategory);

        // A client that is already gone needs no body and no playlist work. 499 is the
        // ASP.NET Core/nginx convention for a client-closed request; never let an abort
        // surface as a 500.
        if (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug(
                "Live index request for session {SessionId} was already aborted by the client.",
                id);
            context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            return Task.FromResult(Results.Empty);
        }

        // Playlists change while recording; this applies to 404s as well.
        context.Response.Headers.CacheControl = "no-cache";

        IResult result = liveIndex.Lookup(channel, id) switch
        {
            LiveIndexLookup.NotFound => Results.NotFound(),
            LiveIndexLookup.Unavailable unavailable =>
                Unavailable(context, logger, id, unavailable.Reason),
            LiveIndexLookup.SessionIndex index => Results.File(
                index.Path, RecordingsMedia.PlaylistContentType, enableRangeProcessing: true),
            LiveIndexLookup.Live live => Serve(logger, id, live),
            _ => throw new InvalidOperationException("Unknown live index lookup."),
        };
        return Task.FromResult(result);
    }

    private static IResult Serve(ILogger logger, string id, LiveIndexLookup.Live live)
    {
        // The snapshot is the last-known-good body; a stale one still plays, so it stays a 200.
        if (live.Stale)
        {
            logger.LogDebug(
                "Live index for session {SessionId} is {AgeSeconds:0.#} s old; serving the last-known-good body.",
                id,
                live.Age.TotalSeconds);
        }

        return new LiveIndexResult(live.Snapshot, live.Stale);
    }

    private static IResult Unavailable(
        HttpContext context, ILogger logger, string id, string reason)
    {
        // A transient outage must never be cached; hls.js retries 5xx playlist loads.
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "1";
        context.Response.Headers.CacheControl = "no-store";
        logger.LogDebug(
            "Live index for session {SessionId} returned 503: {Reason}.",
            id,
            reason);
        return Results.Empty;
    }
}

/// <summary>
/// Serves one immutable snapshot without re-encoding, re-stitching, or touching the session
/// folder. A write that races the client disconnect is not an error.
/// </summary>
internal sealed class LiveIndexResult(LiveIndexSnapshot snapshot, bool stale) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.ContentType = RecordingsMedia.PlaylistContentType;
        context.Response.ContentLength = snapshot.Utf8.Length;
        if (stale)
            context.Response.Headers[LiveIndexEndpoint.StaleHeaderName] = "true";

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
