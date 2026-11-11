using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Hosting;

/// <summary>
/// Serves recording media from the recordings root under <c>/media</c>.
/// Only explicitly allowlisted extensions are exposed; dot-prefixed segments,
/// traversal, metadata files, exports, and temp files are rejected before the
/// static-file provider can reach the filesystem.
/// </summary>
public static class RecordingsMedia
{
    private const string MediaRequestPath = "/media";
    private const string PlaylistExtension = ".m3u8";
    private const string PlaylistCacheControl = "no-cache";
    private const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    private static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PlaylistExtension] = "application/vnd.apple.mpegurl",
            [".m4s"] = "video/iso.segment",
            [".mp4"] = "video/mp4",
            [".jpg"] = "image/jpeg",
            [".png"] = "image/png",
        };

    public static WebApplication UseRecordingsMedia(this WebApplication app)
    {
        string root = Path.GetFullPath(
            app.Services.GetRequiredService<IOptions<StorageOptions>>().Value.RecordingsRoot);
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"{root} is not writable by the app user (UID 1654). " +
                "Use a named volume, or chown a bind-mounted folder to 1654:1654.", ex);
        }

        app.Use((context, next) => GuardAsync(context, next, root));

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(root),
            RequestPath = MediaRequestPath,
            ServeUnknownFileTypes = false,
            ContentTypeProvider = new FileExtensionContentTypeProvider(
                new Dictionary<string, string>(ContentTypes, StringComparer.OrdinalIgnoreCase)),
            OnPrepareResponse = response =>
            {
                // Segments, init files, and thumbnails are content-addressed by session and part,
                // so a year-long immutable cache is safe; playlists change while recording.
                response.Context.Response.Headers.CacheControl =
                    Path.GetExtension(response.File.Name).Equals(PlaylistExtension, StringComparison.OrdinalIgnoreCase)
                        ? PlaylistCacheControl
                        : ImmutableCacheControl;
            },
        });

        return app;
    }

    private static async Task GuardAsync(HttpContext context, RequestDelegate next, string root)
    {
        if (!context.Request.Path.StartsWithSegments(MediaRequestPath, out var remaining))
        {
            await next(context);
            return;
        }

        if (!IsAllowedMediaPath(root, remaining, context.Features.Get<IHttpRequestFeature>()?.RawTarget))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }

    private static bool IsAllowedMediaPath(string root, PathString path, string? rawTarget)
    {
        if (!TrySplitSegments(path.Value, out string[] segments))
            return false;

        // The decoded request path can differ from the raw target when an encoded
        // separator is involved. Validate both so neither representation can hide
        // a dot segment or traversal.
        if (RawRequestPath(rawTarget) is { } rawPath && !TrySplitSegments(rawPath, out _))
            return false;

        if (!ContentTypes.ContainsKey(Path.GetExtension(segments[^1])))
            return false;

        string rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;

        try
        {
            string candidate = Path.GetFullPath(Path.Combine([root, .. segments]));
            return candidate.StartsWith(rootPrefix, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Malformed paths are a 404, not a 500.
            return false;
        }
    }

    private static string? RawRequestPath(string? rawTarget)
    {
        if (string.IsNullOrEmpty(rawTarget) || rawTarget[0] != '/')
            return null;

        int queryIndex = rawTarget.IndexOf('?');
        return queryIndex < 0 ? rawTarget : rawTarget[..queryIndex];
    }

    private static bool TrySplitSegments(string? path, out string[] segments)
    {
        segments = [];

        if (string.IsNullOrEmpty(path) || path[0] != '/')
            return false;

        string decoded;
        try
        {
            // Decode twice: the server normally decodes once, and a second pass
            // also rejects double-encoded traversal.
            decoded = Uri.UnescapeDataString(Uri.UnescapeDataString(path));
        }
        catch (UriFormatException)
        {
            return false;
        }

        string relative = decoded[0] == '/' ? decoded[1..] : decoded;
        if (relative.Length == 0)
            return false;

        segments = relative.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0
                || segment[0] == '.'
                || segment.Contains('\\')
                || segment.Contains('\0')
                || segment.Any(char.IsControl))
                return false;
        }

        return true;
    }
}
