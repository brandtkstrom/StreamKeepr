namespace StreamKeepr.Infrastructure.Twitch.Chat;

/// <summary>A parsed IRC line: optional IRCv3 tags, prefix, command, params, and trailing.</summary>
public sealed record IrcMessage(
    IReadOnlyDictionary<string, string> Tags,
    string? Prefix,
    string Command,
    IReadOnlyList<string> Parameters,
    string? Trailing)
{
    public string? Tag(string name) =>
        Tags.TryGetValue(name, out string? value) ? value : null;

    public string? Parameter(int index) =>
        index >= 0 && index < Parameters.Count ? Parameters[index] : null;
}
