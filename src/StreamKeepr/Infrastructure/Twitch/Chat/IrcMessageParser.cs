namespace StreamKeepr.Infrastructure.Twitch.Chat;

/// <summary>
/// Parses one CRLF-delimited IRC line. Malformed lines return false; the caller logs and skips
/// them. The parser owns no I/O and is safe to call concurrently.
/// </summary>
public static class IrcMessageParser
{
    public static bool TryParse(string line, out IrcMessage? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        ReadOnlySpan<char> rest = line.AsSpan();
        while (rest.Length > 0 && (rest[^1] == '\r' || rest[^1] == '\n'))
            rest = rest[..^1];
        if (rest.IsEmpty)
            return false;

        IReadOnlyDictionary<string, string> tags = IrcTagParser.Parse(null);
        if (rest[0] == '@')
        {
            int space = rest.IndexOf(' ');
            if (space <= 1)
                return false;

            tags = IrcTagParser.Parse(rest[1..space].ToString());
            rest = rest[(space + 1)..].TrimStart(' ');
        }

        string? prefix = null;
        if (!rest.IsEmpty && rest[0] == ':')
        {
            int space = rest.IndexOf(' ');
            if (space <= 1)
                return false;

            prefix = rest[1..space].ToString();
            rest = rest[(space + 1)..].TrimStart(' ');
        }

        if (rest.IsEmpty)
            return false;

        int commandEnd = rest.IndexOf(' ');
        string command = (commandEnd < 0 ? rest : rest[..commandEnd]).ToString().ToUpperInvariant();
        if (command.Length == 0)
            return false;

        var parameters = new List<string>(4);
        string? trailing = null;
        rest = commandEnd < 0 ? ReadOnlySpan<char>.Empty : rest[(commandEnd + 1)..];

        while (!rest.IsEmpty)
        {
            rest = rest.TrimStart(' ');
            if (rest.IsEmpty)
                break;

            if (rest[0] == ':')
            {
                trailing = rest[1..].ToString();
                break;
            }

            int space = rest.IndexOf(' ');
            if (space < 0)
            {
                parameters.Add(rest.ToString());
                break;
            }

            parameters.Add(rest[..space].ToString());
            rest = rest[(space + 1)..];
        }

        message = new IrcMessage(tags, prefix, command, parameters, trailing);
        return true;
    }
}
