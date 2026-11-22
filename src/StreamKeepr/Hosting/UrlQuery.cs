using Microsoft.AspNetCore.WebUtilities;

namespace StreamKeepr.Hosting;

/// <summary>Query-string edits on a URL that is already a string (a return URL, an authorize URL).</summary>
public static class UrlQuery
{
    /// <summary>
    /// The URL with every value of <paramref name="key"/> (compared ignoring case) removed from its
    /// query; the path and the other parameters, in order, are kept. A URL without a query is
    /// returned as it is.
    /// </summary>
    public static string Without(string url, string key)
    {
        int queryIndex = url.IndexOf('?');
        if (queryIndex < 0)
            return url;

        var kept = QueryHelpers.ParseQuery(url[queryIndex..])
            .Where(pair => !string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value)));
        return url[..queryIndex] + QueryString.Create(kept);
    }

    /// <summary>The URL with <paramref name="key"/> set to exactly one <paramref name="value"/>.</summary>
    public static string With(string url, string key, string value) =>
        QueryHelpers.AddQueryString(Without(url, key), key, value);
}
