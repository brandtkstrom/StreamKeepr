using System.Text;

namespace StreamKeepr.Ui;

/// <summary>
/// Reads and writes the library view keys (<c>q</c>, <c>state</c>, <c>sort</c>) in a URL so a
/// view can be bookmarked, shared, and restored on reload. Missing or invalid values fall back
/// to no search, no filter, and newest first; defaults are never written back.
/// </summary>
public static class LibraryUrl
{
    public const string SearchKey = "q";
    public const string StateKey = "state";
    public const string SortKey = "sort";

    public static LibraryQuery Parse(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        string? search = null;
        string? state = null;
        string sort = "newest";

        foreach ((string key, string value) in Pairs(QueryOf(uri)))
        {
            switch (key)
            {
                case SearchKey:
                    search = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                    break;
                case StateKey:
                    state = value.Trim().ToLowerInvariant() switch
                    {
                        "recording" => "recording",
                        "saved" => "saved",
                        _ => null,
                    };
                    break;
                case SortKey:
                    sort = value.Trim().ToLowerInvariant() switch
                    {
                        "oldest" => "oldest",
                        "az" => "az",
                        _ => "newest",
                    };
                    break;
            }
        }

        return new LibraryQuery(state, search, sort);
    }

    /// <summary>
    /// Returns the path and query of <paramref name="uri"/> with the library keys replaced by
    /// <paramref name="query"/>. Other query keys are kept in their original order.
    /// </summary>
    public static string With(string uri, LibraryQuery query)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(query);

        string path = PathOf(uri);
        var builder = new StringBuilder();

        foreach (string raw in QueryOf(uri).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string key = Decode(raw.Split('=', 2)[0]);
            if (key is SearchKey or StateKey or SortKey)
                continue;

            Append(builder, raw);
        }

        LibraryQuery normalized = Parse("?" + Encode(query));
        if (normalized.Search is { } search)
            Append(builder, $"{SearchKey}={Uri.EscapeDataString(search)}");
        if (normalized.State is { } state)
            Append(builder, $"{StateKey}={state}");
        if (normalized.Sort != "newest")
            Append(builder, $"{SortKey}={normalized.Sort}");

        return builder.Length == 0 ? path : $"{path}?{builder}";
    }

    private static string Encode(LibraryQuery query) =>
        $"{SearchKey}={Uri.EscapeDataString(query.Search ?? "")}" +
        $"&{StateKey}={Uri.EscapeDataString(query.State ?? "")}" +
        $"&{SortKey}={Uri.EscapeDataString(query.Sort ?? "")}";

    private static void Append(StringBuilder builder, string pair)
    {
        if (builder.Length > 0)
            builder.Append('&');
        builder.Append(pair);
    }

    private static IEnumerable<(string Key, string Value)> Pairs(string query)
    {
        foreach (string raw in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = raw.Split('=', 2);
            yield return (Decode(parts[0]), parts.Length > 1 ? Decode(parts[1]) : "");
        }
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static string StripFragment(string uri)
    {
        int hash = uri.IndexOf('#', StringComparison.Ordinal);
        return hash >= 0 ? uri[..hash] : uri;
    }

    private static string QueryOf(string uri)
    {
        uri = StripFragment(uri);
        int mark = uri.IndexOf('?', StringComparison.Ordinal);
        return mark >= 0 ? uri[(mark + 1)..] : "";
    }

    private static string PathOf(string uri)
    {
        uri = StripFragment(uri);
        int mark = uri.IndexOf('?', StringComparison.Ordinal);
        string beforeQuery = mark >= 0 ? uri[..mark] : uri;

        if (Uri.TryCreate(beforeQuery, UriKind.Absolute, out Uri? absolute))
            return absolute.AbsolutePath;

        return beforeQuery.Length == 0 ? "/" : beforeQuery;
    }
}
