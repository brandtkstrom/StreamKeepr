using System.Globalization;

namespace StreamKeepr.Ui;

/// <summary>Pure display helpers shared by the shell and feature UIs.</summary>
public static class Format
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Timecode(TimeSpan value)
    {
        var total = value < TimeSpan.Zero ? 0 : (long)Math.Floor(value.TotalSeconds);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;

        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:00}");
    }

    public static string Timecode(double seconds) => Timecode(TimeSpan.FromSeconds(seconds));

    public static string Duration(TimeSpan value)
    {
        var total = value < TimeSpan.Zero ? 0 : (long)Math.Floor(value.TotalSeconds);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;

        if (hours > 0)
            return string.Create(CultureInfo.InvariantCulture, $"{hours}h {minutes:00}m");
        if (minutes > 0)
            return string.Create(CultureInfo.InvariantCulture, $"{minutes}m");
        return string.Create(CultureInfo.InvariantCulture, $"{total}s");
    }

    public static string Duration(double seconds) => Duration(TimeSpan.FromSeconds(seconds));

    public static string DayHeader(DateTimeOffset when, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var localWhen = TimeZoneInfo.ConvertTime(when, timeZone);
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);

        if (localWhen.Date == localNow.Date)
            return "Today";
        if (localWhen.Date == localNow.Date.AddDays(-1))
            return "Yesterday";

        return localWhen.ToString("ddd, MMM d", CultureInfo.InvariantCulture);
    }

    public static string Bytes(long? bytes)
    {
        if (bytes is null || bytes < 0)
            return "—";

        double value = bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{value:0} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {ByteUnits[unit]}");
    }
}
