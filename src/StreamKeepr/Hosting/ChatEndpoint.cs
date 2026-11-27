using System.Globalization;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Hosting;

/// <summary>
/// Public read-only chat window. Viewing is open in Stream Keepr, so this follows media/progress;
/// the private-home-use scope is the exposure boundary.
/// </summary>
public static class ChatEndpoint
{
    public const string Route = "/api/recordings/{id}/chat";
    public const int DefaultLimit = 300;
    public const int MaxLimit = 1000;
    public const double DefaultWindowSeconds = 120;
    public const double MaxWindowSeconds = 600;

    public static IResult Handle(
        string id,
        HttpContext context,
        RecordingLibrary library,
        ChatReader reader)
    {
        context.Response.Headers.CacheControl = "no-store";

        if (library.Find(id) is not { } summary)
            return Results.NotFound();

        if (!TryBool(context, "tail", defaultValue: false, out bool tail))
            return Results.BadRequest();

        if (tail)
        {
            if (!TryDouble(context, "toSec", required: true, out double to) ||
                !TryInt(context, "limit", DefaultLimit, out int limit) ||
                !TryDouble(context, "syncOffsetSec", required: false, out double offset))
            {
                return Results.BadRequest();
            }

            if (to < 0 ||
                limit is < 1 or > MaxLimit ||
                Math.Abs(offset) > ChatReader.MaxSyncOffsetSeconds)
            {
                return Results.BadRequest();
            }

            return Results.Json(reader.QueryTail(summary, to, limit, offset));
        }

        if (!TryDouble(context, "fromSec", required: true, out double from) ||
            from < 0 ||
            !TryDouble(context, "toSec", required: false, out double windowTo) ||
            !TryInt(context, "limit", DefaultLimit, out int windowLimit) ||
            !TryDouble(context, "syncOffsetSec", required: false, out double windowOffset))
        {
            return Results.BadRequest();
        }

        double defaultTo = from + DefaultWindowSeconds;
        windowTo = context.Request.Query.ContainsKey("toSec") ? windowTo : defaultTo;
        if (!double.IsFinite(windowTo) || windowTo <= from || windowTo - from > MaxWindowSeconds)
            return Results.BadRequest();

        if (windowLimit is < 1 or > MaxLimit ||
            Math.Abs(windowOffset) > ChatReader.MaxSyncOffsetSeconds)
        {
            return Results.BadRequest();
        }

        ChatQueryResult result = reader.Query(summary, from, windowTo, windowLimit, windowOffset);
        return Results.Json(result);
    }

    private static bool TryDouble(
        HttpContext context, string key, bool required, out double value)
    {
        value = 0;
        if (!context.Request.Query.TryGetValue(key, out var raw))
            return !required;

        return double.TryParse(
            raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value);
    }


    private static bool TryBool(
        HttpContext context, string key, bool defaultValue, out bool value)
    {
        value = defaultValue;
        if (!context.Request.Query.TryGetValue(key, out var raw))
            return true;

        return bool.TryParse(raw.ToString(), out value);
    }

    private static bool TryInt(
        HttpContext context, string key, int defaultValue, out int value)
    {
        value = defaultValue;
        if (!context.Request.Query.TryGetValue(key, out var raw))
            return true;

        return int.TryParse(
            raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
