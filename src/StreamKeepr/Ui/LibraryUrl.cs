using System.Text;

namespace StreamKeepr.Ui;

/// <summary>
/// Reads and writes the library view keys (<c>q</c>, <c>state</c>, <c>sort</c>) in a URL so a
/// view can be bookmarked, shared, and restored on reload. It also carries the player's one-shot
/// <c>autoplay</c> selection intent, which is not a library filter. Missing or invalid values fall
/// back to no search, no filter, and newest first; defaults are never written back.
/// </summary>
public static class LibraryUrl
{
    public const string SearchKey = "q";
    public const string StateKey = "state";
    public const string SortKey = "sort";

    /// <summary>
    /// The one-shot player-open intent a library selection adds to its watch link. It is a
    /// player key, not a library filter: <see cref="Parse"/> ignores it.
    /// </summary>
    public const string AutoplayKey = "autoplay";

    public static LibraryQuery Parse(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        string? search = null;
        string? state = null;
        string sort = "newest";

        foreach ((string key, string value) in QueryPairs.Pairs(QueryOf(uri)))
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
    /// The recording id when <paramref name="uri"/> is a watch page (<c>/r/{id}</c>), else
    /// <c>null</c>. The shell uses it to pick the watch-page drawer and top bar.
    /// </summary>
    public static string? RecordingId(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        string path = Uri.TryCreate(uri, UriKind.Absolute, out Uri? absolute)
            ? absolute.AbsolutePath
            : uri.Split('?', '#')[0];
        const string prefix = "/r/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        string id = path[prefix.Length..];
        return id.Length == 0 || id.Contains('/') ? null : Uri.UnescapeDataString(id);
    }

    /// <summary>
    /// True when <paramref name="uri"/> asks the player to start playback once, i.e. the
    /// navigation came from a library selection rather than a direct link or reload.
    /// </summary>
    public static bool AutoplayRequested(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        foreach ((string key, string value) in QueryPairs.Pairs(QueryOf(uri)))
        {
            if (key == AutoplayKey && IsAutoplayIntent(value))
                return true;
        }

        return false;
    }

    /// <summary>True when a query value is the player's one-shot autoplay marker (<c>1</c>).</summary>
    public static bool IsAutoplayIntent(string? value) =>
        string.Equals(value?.Trim(), "1", StringComparison.Ordinal);

    /// <summary>
    /// Adds the one-shot player-open intent to a selection link, replacing any existing one.
    /// </summary>
    public static string WithAutoplay(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        string path = PathOf(uri);
        StringBuilder builder = QueryWithoutAutoplay(uri);
        QueryPairs.Append(builder, $"{AutoplayKey}=1");
        return $"{path}?{builder}";
    }

    /// <summary>Removes the player-open intent once Home has consumed it.</summary>
    public static string WithoutAutoplay(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        string path = PathOf(uri);
        StringBuilder builder = QueryWithoutAutoplay(uri);
        return builder.Length == 0 ? path : $"{path}?{builder}";
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
        StringBuilder builder = QueryPairs.Without(
            QueryOf(uri), key => key is SearchKey or StateKey or SortKey or AutoplayKey);

        LibraryQuery normalized = Parse("?" + Encode(query));
        if (normalized.Search is { } search)
            QueryPairs.Append(builder, $"{SearchKey}={Uri.EscapeDataString(search)}");
        if (normalized.State is { } state)
            QueryPairs.Append(builder, $"{StateKey}={state}");
        if (normalized.Sort != "newest")
            QueryPairs.Append(builder, $"{SortKey}={normalized.Sort}");

        return builder.Length == 0 ? path : $"{path}?{builder}";
    }

    private static StringBuilder QueryWithoutAutoplay(string uri) =>
        QueryPairs.Without(QueryOf(uri), key => key == AutoplayKey);

    private static string Encode(LibraryQuery query) =>
        $"{SearchKey}={Uri.EscapeDataString(query.Search ?? "")}" +
        $"&{StateKey}={Uri.EscapeDataString(query.State ?? "")}" +
        $"&{SortKey}={Uri.EscapeDataString(query.Sort ?? "")}";

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

        // Extract the path only from an absolute http(s) URL. A rooted path such as
        // /r/a%20b must be kept exactly: Uri reads it as a file URI on some platforms and
        // would escape the percent sign a second time.
        if (Uri.TryCreate(beforeQuery, UriKind.Absolute, out Uri? absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.AbsolutePath;
        }

        return beforeQuery.Length == 0 ? "/" : beforeQuery;
    }
}
