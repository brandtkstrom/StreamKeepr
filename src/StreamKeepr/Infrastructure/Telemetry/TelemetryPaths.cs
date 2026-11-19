using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace StreamKeepr.Infrastructure.Telemetry;

/// <summary>
/// The single path policy for traces and HTTP metrics. Sensitive and long-lived requests are
/// excluded; every other traced request reports its route template instead of its literal path.
/// </summary>
internal static class TelemetryPaths
{
    private static readonly string[] Excluded =
    [
        "/health/live",
        "/health/ready",
        "/media",
        "/_blazor",
        "/signin-twitch",
    ];

    public static bool ShouldTrace(HttpContext context) => !IsExcluded(context.Request.Path);

    public static bool DisableHttpMetrics(PathString path) => IsExcluded(path);

    private static bool IsExcluded(PathString path) =>
        Excluded.Any(excluded => path.StartsWithSegments(excluded));

    public static void RewriteUrlPath(Activity activity, HttpResponse response)
    {
        string? route = response.HttpContext.GetEndpoint() is RouteEndpoint endpoint
            ? endpoint.RoutePattern.RawText
            : activity.GetTagItem("http.route") as string;

        activity.SetTag("url.path", route is { Length: > 0 } ? route : "/[unrouted]");
    }
}
