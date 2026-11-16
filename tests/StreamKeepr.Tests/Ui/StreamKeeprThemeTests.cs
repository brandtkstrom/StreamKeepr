using System.Reflection;
using MudBlazor;
using MudBlazor.Utilities;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

/// <summary>
/// <c>StreamKeeprTheme</c> is hand-written from <c>design-system/tokens.json</c> (ADR 0012).
/// These tests fail when either side changes without the other.
/// </summary>
public sealed partial class StreamKeeprThemeTests
{
    private static readonly DesignTokens Tokens = DesignTokens.Load();
    private static readonly Palette Palette = StreamKeeprTheme.Theme.PaletteDark;

    private static readonly PropertyInfo[] PaletteProperties = typeof(PaletteDark)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanRead
            && (property.PropertyType == typeof(MudColor) || property.PropertyType == typeof(double)))
        .ToArray();

    [Fact]
    public void Every_palette_token_matches_the_theme()
    {
        var mismatches = new List<string>();
        int compared = 0;
        foreach ((string name, string value) in PaletteTokens())
        {
            PropertyInfo property = PaletteProperties.Single(p => p.Name == DesignTokens.PascalCase(name));
            compared++;
            object? actual = property.GetValue(Palette);
            bool equal = property.PropertyType == typeof(double)
                ? Equals(actual, double.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                : new MudColor(value).Equals(actual);
            if (!equal)
                mismatches.Add($"{name}: tokens.json {value}, theme {actual}");
        }

        Assert.Empty(mismatches);
        // Guards the name mapping: if it broke, nothing would be compared and the test would pass.
        Assert.True(compared >= 30, $"only {compared} palette tokens were compared");
    }

    [Fact]
    public void Every_palette_value_the_theme_sets_comes_from_a_token()
    {
        var defaults = new PaletteDark();
        HashSet<string> tokenised = PaletteTokens().Select(t => DesignTokens.PascalCase(t.Name)).ToHashSet();

        string[] untokenised = PaletteProperties
            .Where(property => !Equals(property.GetValue(Palette), property.GetValue(defaults)))
            .Select(property => property.Name)
            .Where(name => !tokenised.Contains(name))
            .ToArray();

        Assert.Empty(untokenised);
    }

    [Fact]
    public void Layout_properties_match_the_size_and_radius_tokens()
    {
        IReadOnlyDictionary<string, string> size = Tokens.Group("size");
        IReadOnlyDictionary<string, string> radius = Tokens.Group("radius");
        LayoutProperties layout = StreamKeeprTheme.Theme.LayoutProperties;

        Assert.Equal(radius["radius-md"], layout.DefaultBorderRadius);
        Assert.Equal(size["appbar-height"], layout.AppbarHeight);
        Assert.Equal(size["drawer-width"], layout.DrawerWidthLeft);
        Assert.Equal(size["drawer-mini-width"], layout.DrawerMiniWidthLeft);
    }

    [Fact]
    public void Buttons_keep_sentence_case()
    {
        Assert.Equal("none", Tokens.TypeStyles("MudBlazor text")["button"].GetProperty("textTransform").GetString());
        Assert.Equal("none", StreamKeeprTheme.Theme.Typography.Button.TextTransform);
    }

    [Fact]
    public void Every_MudBlazor_type_style_matches_the_theme_typography()
    {
        Typography typography = StreamKeeprTheme.Theme.Typography;
        var mismatches = new List<string>();
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> styles = Tokens.TypeStyles("MudBlazor headings", "MudBlazor text");
        // H1-H6 and the seven text slots; guards the group names so the loop cannot run empty.
        Assert.Equal(13, styles.Count);
        foreach ((string name, System.Text.Json.JsonElement style) in styles)
        {
            var slot = (BaseTypography)typeof(Typography).GetProperty(DesignTokens.PascalCase(name))!.GetValue(typography)!;
            Compare(name, "fontSize", style.GetProperty("fontSize").GetString(), slot.FontSize);
            Compare(name, "lineHeight", style.GetProperty("lineHeight").GetString(), slot.LineHeight);
            Compare(name, "fontWeight", style.GetProperty("fontWeight").ToString(), slot.FontWeight);
            Compare(name, "letterSpacing", style.GetProperty("letterSpacing").GetString(), slot.LetterSpacing);
            if (style.TryGetProperty("textTransform", out System.Text.Json.JsonElement transform))
                Compare(name, "textTransform", transform.GetString(), slot.TextTransform);
        }

        Compare("default", "fontFamily", Tokens.SansFamily, string.Join(", ", typography.Default.FontFamily ?? []));
        Assert.Empty(mismatches);

        void Compare(string style, string field, string? expected, string? actual)
        {
            if (!string.Equals(Normalise(expected), Normalise(actual), StringComparison.Ordinal))
                mismatches.Add($"{style}.{field}: tokens.json {expected}, theme {actual}");
        }
    }

    // MudBlazor writes "-.01562em" and "0"; tokens.json writes "-0.01562em" and "0px".
    private static string? Normalise(string? value) => value?
        .Replace("-.", "-0.", StringComparison.Ordinal)
        .Replace(" ", "", StringComparison.Ordinal) switch
    {
        "0" or "0px" or "0em" => "0",
        var other when other is not null && other.StartsWith('.') => "0" + other,
        var other => other,
    };

    [System.Text.RegularExpressions.GeneratedRegex(@"-(accent|darken|lighten)-\d$|-default$")]
    private static partial System.Text.RegularExpressions.Regex SourceToken();

    private static IEnumerable<(string Name, string Value)> PaletteTokens()
    {
        HashSet<string> names = PaletteProperties.Select(p => p.Name).ToHashSet();
        foreach ((string name, string value) in Tokens.Colors)
        {
            // Source tokens are named after MudBlazor.Colors constants (gray-default = Colors.Gray.Default);
            // only role tokens name a Palette slot, even where a source name happens to match one.
            if (!SourceToken().IsMatch(name) && names.Contains(DesignTokens.PascalCase(name)))
                yield return (name, value);
        }

        foreach ((string name, string value) in Tokens.Group("opacity"))
        {
            if (names.Contains(DesignTokens.PascalCase(name)))
                yield return (name, value);
        }
    }
}
