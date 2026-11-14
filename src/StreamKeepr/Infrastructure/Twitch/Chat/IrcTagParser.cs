using System.Text;

namespace StreamKeepr.Infrastructure.Twitch.Chat;

/// <summary>
/// Parses the IRCv3 tag block. Split on semicolons before unescaping; the last duplicate key wins.
/// Values are UTF-8 and use the IRCv3 escape set.
/// </summary>
public static class IrcTagParser
{
    public static IReadOnlyDictionary<string, string> Parse(string? block)
    {
        if (string.IsNullOrEmpty(block))
            return Empty;

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in block.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = equals < 0 ? pair : pair[..equals];
            if (key.Length == 0)
                continue;

            string value = equals < 0 ? "" : Unescape(pair[(equals + 1)..]);
            tags[key] = value;
        }

        return tags;
    }

    public static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0)
            return value;

        var builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }

            if (++i >= value.Length)
                break;

            builder.Append(value[i] switch
            {
                ':' => ';',
                's' => ' ',
                '\\' => '\\',
                'r' => '\r',
                'n' => '\n',
                _ => value[i],
            });
        }

        return builder.ToString();
    }

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
