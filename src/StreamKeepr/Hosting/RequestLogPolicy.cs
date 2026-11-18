using Serilog.Events;

namespace StreamKeepr.Hosting;

/// <summary>
/// Chooses the level of the one request-completion event. Failures are always visible:
/// 5xx and unhandled exceptions are <see cref="LogEventLevel.Error"/> and 4xx is
/// <see cref="LogEventLevel.Warning"/> on every path. Successful requests on quiet paths
/// (health probes, media, Blazor transport, static assets, player APIs) are
/// <see cref="LogEventLevel.Verbose"/>, which the shipped minimum level drops before the
/// event is built; everything else is <see cref="LogEventLevel.Information"/>.
/// </summary>
internal static class RequestLogPolicy
{
    // Sources of truth: MapHealthChecks in Program.cs, RecordingsMedia and LiveIndexEndpoint
    // (/media), and the ProgressEndpoint/ChatEndpoint/EmoteEndpoint routes (/api/recordings).
    // RequestLogPolicyTests pins the endpoint routes to these prefixes.
    private static readonly PathString[] QuietPrefixes =
    [
        "/health",
        "/media",
        "/_blazor",
        "/_framework",
        "/_content",
        "/api/recordings",
    ];

    /// <summary>The <c>RequestLoggingOptions.GetLevel</c> callback.</summary>
    public static LogEventLevel GetLevel(HttpContext context, double elapsedMs, Exception? exception) =>
        GetLevel(context.Request.Path, context.Response.StatusCode, exception);

    public static LogEventLevel GetLevel(PathString path, int statusCode, Exception? exception)
    {
        if (exception is not null || statusCode >= StatusCodes.Status500InternalServerError)
            return LogEventLevel.Error;

        if (statusCode >= StatusCodes.Status400BadRequest)
            return LogEventLevel.Warning;

        return IsQuiet(path) ? LogEventLevel.Verbose : LogEventLevel.Information;
    }

    private static bool IsQuiet(PathString path) =>
        QuietPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
        || IsStaticAsset(path);

    // MapStaticAssets serves wwwroot from the root, so static files are recognized by
    // extension. Media extensions are covered by the /media prefix, not listed here.
    private static bool IsStaticAsset(PathString path) =>
        path.HasValue && StaticAssetExtensions.Contains(Path.GetExtension(path.Value));

    private static readonly HashSet<string> StaticAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".css", ".map", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".webp",
        ".webmanifest", ".woff", ".woff2", ".ttf", ".otf",
    };
}
