using System.Text;

namespace StreamKeepr.Ui;

/// <summary>
/// Query-string parsing shared by the URL helpers (<see cref="LibraryUrl"/>,
/// <see cref="SettingsSheetUrl"/>), so both read and rewrite queries the same way: <c>+</c> is a
/// space, and keys they do not own keep their original encoding and order.
/// </summary>
internal static class QueryPairs
{
    /// <summary>The decoded key/value pairs of <paramref name="query"/> (a leading <c>?</c> is ignored).</summary>
    public static IEnumerable<(string Key, string Value)> Pairs(string query)
    {
        foreach (string raw in Raw(query))
        {
            string[] parts = raw.Split('=', 2);
            yield return (Decode(parts[0]), parts.Length > 1 ? Decode(parts[1]) : "");
        }
    }

    /// <summary>The raw pairs of <paramref name="query"/> whose decoded key <paramref name="drop"/> rejects, in order.</summary>
    public static StringBuilder Without(string query, Func<string, bool> drop)
    {
        var builder = new StringBuilder();
        foreach (string raw in Raw(query))
        {
            if (!drop(Decode(raw.Split('=', 2)[0])))
                Append(builder, raw);
        }

        return builder;
    }

    /// <summary>Appends <paramref name="pair"/> (already encoded) with an <c>&amp;</c> separator.</summary>
    public static void Append(StringBuilder builder, string pair)
    {
        if (builder.Length > 0)
            builder.Append('&');
        builder.Append(pair);
    }

    private static string[] Raw(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}
