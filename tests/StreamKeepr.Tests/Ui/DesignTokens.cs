using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamKeepr.Tests.Ui;

/// <summary>
/// Reads <c>design-system/tokens.json</c>, the single source of design values (ADR 0012),
/// so tests can compare the hand-written theme against it.
/// </summary>
internal sealed partial class DesignTokens
{
    private readonly Dictionary<string, string> colors;
    private readonly JsonElement root;

    private DesignTokens(JsonElement root)
    {
        this.root = root;
        colors = root.GetProperty("color").GetProperty("tokens").EnumerateArray()
            .ToDictionary(
                token => token.GetProperty("name").GetString()!,
                token => token.GetProperty("value").GetProperty("dark").GetString()!);
    }

    public static DesignTokens Load()
    {
        string path = Path.Combine(TestPaths.RepositoryRoot(), "design-system", "tokens.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return new DesignTokens(document.RootElement.Clone());
    }

    /// <summary>Colour token names with their <c>{reference}</c> values resolved.</summary>
    public IReadOnlyDictionary<string, string> Colors =>
        colors.Keys.ToDictionary(name => name, Color);

    public string Color(string name)
    {
        var seen = new HashSet<string>();
        string value = colors[name];
        while (ReferencePattern().Match(value) is { Success: true } match)
        {
            Assert.True(seen.Add(value), $"token {name} has a reference cycle");
            value = colors[match.Groups[1].Value];
        }

        return value;
    }

    /// <summary>Tokens of a non-colour group (<c>size</c>, <c>radius</c>, <c>opacity</c>) by name.</summary>
    public IReadOnlyDictionary<string, string> Group(string group) =>
        root.GetProperty(group).GetProperty("tokens").EnumerateArray()
            .ToDictionary(
                token => token.GetProperty("name").GetString()!,
                token => token.GetProperty("value").ToString());

    /// <summary>The font stack a type group names, such as <c>sans</c>.</summary>
    public string Family(string name) =>
        root.GetProperty("type").GetProperty("families").GetProperty(name).GetString()!;

    /// <summary>Every type style in the named groups, by style name, with its group's font stack.</summary>
    public IReadOnlyDictionary<string, TypeStyle> TypeStyles(params string[] groups) =>
        root.GetProperty("type").GetProperty("groups").EnumerateArray()
            .Where(group => groups.Contains(group.GetProperty("name").GetString()))
            .SelectMany(group => group.GetProperty("styles").EnumerateArray()
                .Select(style => new TypeStyle(style, Family(group.GetProperty("family").GetString()!))))
            .ToDictionary(style => style.Style.GetProperty("name").GetString()!);

    /// <summary><c>drawer-background</c> → <c>DrawerBackground</c>.</summary>
    public static string PascalCase(string kebab) =>
        string.Concat(kebab.Split('-').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    [GeneratedRegex(@"^\{([a-z0-9-]+)\}$")]
    private static partial Regex ReferencePattern();
}

/// <summary>A type style from <c>tokens.json</c> and the font stack its group uses.</summary>
internal sealed record TypeStyle(JsonElement Style, string FontFamily);
