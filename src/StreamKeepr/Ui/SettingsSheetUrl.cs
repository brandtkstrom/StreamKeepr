using System.Text;

namespace StreamKeepr.Ui;

/// <summary>
/// The settings sheet's URL state (design system: SettingsSheet). <c>?settings=open</c> on the
/// current page shows the sheet over it, so Back closes it and the state can be shared; only
/// the query changes, so the page and its player stay. The <c>/settings</c> page renders the
/// same sections itself, so the sheet never opens there.
/// </summary>
public static class SettingsSheetUrl
{
    private const string Key = "settings";
    private const string OpenValue = "open";

    /// <summary>The Settings page; <c>AppShell.razor.js</c> mirrors it to claim links to it.</summary>
    public const string PagePath = "/settings";

    /// <summary>Whether the absolute <paramref name="uri"/> shows the sheet.</summary>
    public static bool IsOpen(string uri)
    {
        Uri absolute = new(uri, UriKind.Absolute);
        if (IsPage(absolute))
            return false;

        return QueryPairs.Pairs(absolute.Query).Any(pair =>
            pair.Key == Key && string.Equals(pair.Value, OpenValue, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The base-relative path and query of <paramref name="uri"/> with the sheet open.</summary>
    public static string Open(string uri) => With(uri, open: true);

    /// <summary>The base-relative path and query of <paramref name="uri"/> with the sheet closed.</summary>
    public static string Close(string uri) => With(uri, open: false);

    /// <summary>Whether the absolute <paramref name="uri"/> is the Settings page itself.</summary>
    public static bool IsSettingsPage(string uri) => IsPage(new Uri(uri, UriKind.Absolute));

    private static bool IsPage(Uri uri) =>
        string.Equals(uri.AbsolutePath, PagePath, StringComparison.OrdinalIgnoreCase);

    private static string With(string uri, bool open)
    {
        Uri absolute = new(uri, UriKind.Absolute);
        StringBuilder builder = QueryPairs.Without(absolute.Query, key => key == Key);
        if (open)
            QueryPairs.Append(builder, $"{Key}={OpenValue}");

        return builder.Length == 0 ? absolute.AbsolutePath : $"{absolute.AbsolutePath}?{builder}";
    }
}
