using System.Reflection;
using MudBlazor;
using MudBlazor.Utilities;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

/// <summary>
/// <c>StreamKeeprTheme</c> is hand-written from <c>design-system/tokens.json</c> (ADR 0012).
/// These tests fail when either side changes without the other.
/// </summary>
public sealed class StreamKeeprThemeTests
{
    private static readonly DesignTokens Tokens = DesignTokens.Load();
    private static readonly Palette Palette = StreamKeeprTheme.Theme.PaletteDark;

    private static readonly PropertyInfo[] PaletteProperties = typeof(PaletteDark)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanRead
            && (property.PropertyType == typeof(MudColor)
                || property.PropertyType == typeof(string)
                || property.PropertyType == typeof(double)))
        .ToArray();

    // Source tokens are named after MudBlazor.Colors constants. This one also spells a Palette slot
    // (Palette.GrayDefault, a legacy grey) but is not a role token, so the theme never sets it.
    private static readonly HashSet<string> SourceTokensNamingAPaletteSlot = ["gray-default"];

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
            bool equal = actual switch
            {
                double number => number == double.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
                string colour => new MudColor(value).Equals(new MudColor(colour)),
                _ => new MudColor(value).Equals(actual),
            };
            if (!equal)
                mismatches.Add($"{name}: tokens.json {value}, theme {actual}");
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
        // Guards the name mapping: if it broke, nothing would be compared and the test would pass.
        Assert.True(compared >= 35, $"only {compared} palette tokens were compared");
    }

    [Fact]
    public void Every_palette_value_the_theme_sets_comes_from_a_token()
    {
        var defaults = new PaletteDark();
        HashSet<string> tokenised = PaletteTokens().Select(t => DesignTokens.PascalCase(t.Name)).ToHashSet();

        string[] untokenised = PaletteProperties
            .Where(property => !IsDerivedShade(property.Name))
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
        Assert.Equal("none", Tokens.TypeStyles("MudBlazor text")["button"].Style.GetProperty("textTransform").GetString());
        Assert.Equal("none", StreamKeeprTheme.Theme.Typography.Button.TextTransform);
    }

    [Fact]
    public void Every_MudBlazor_type_style_matches_the_theme_typography()
    {
        Typography typography = StreamKeeprTheme.Theme.Typography;
        var mismatches = new List<string>();
        IReadOnlyDictionary<string, TypeStyle> styles = Tokens.TypeStyles("MudBlazor headings", "MudBlazor text");
        // H1-H6 and the seven text slots; guards the group names so the loop cannot run empty.
        Assert.Equal(13, styles.Count);
        foreach ((string name, TypeStyle style) in styles)
            CompareSlot(name, style, (BaseTypography)typeof(Typography).GetProperty(DesignTokens.PascalCase(name))!.GetValue(typography)!);

        // Typography.Default is the body text MudBlazor applies to <body>: tokens.json's body2.
        CompareSlot("default", styles["body2"], typography.Default);
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));

        void CompareSlot(string name, TypeStyle typeStyle, BaseTypography slot)
        {
            System.Text.Json.JsonElement style = typeStyle.Style;
            // A slot left null renders with Default's family (MudThemeProvider emits it for the slot).
            Compare(name, "fontFamily", typeStyle.FontFamily, string.Join(", ", slot.FontFamily ?? typography.Default.FontFamily ?? []));
            Compare(name, "fontSize", style.GetProperty("fontSize").GetString(), slot.FontSize);
            Compare(name, "lineHeight", style.GetProperty("lineHeight").GetString(), slot.LineHeight);
            Compare(name, "fontWeight", style.GetProperty("fontWeight").ToString(), slot.FontWeight);
            Compare(name, "letterSpacing", style.GetProperty("letterSpacing").GetString(), slot.LetterSpacing);
            if (style.TryGetProperty("textTransform", out System.Text.Json.JsonElement transform))
                Compare(name, "textTransform", transform.GetString(), slot.TextTransform);
        }

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

    // MudBlazor computes PrimaryDarken, PrimaryLighten and so on from the base colour; the theme never sets them.
    private static bool IsDerivedShade(string name) =>
        (name.EndsWith("Darken", StringComparison.Ordinal) || name.EndsWith("Lighten", StringComparison.Ordinal))
        && PaletteProperties.Any(p => name == p.Name + "Darken" || name == p.Name + "Lighten");

    private static IEnumerable<(string Name, string Value)> PaletteTokens()
    {
        HashSet<string> names = PaletteProperties.Select(p => p.Name).ToHashSet();
        foreach ((string name, string value) in Tokens.Colors)
        {
            if (!SourceTokensNamingAPaletteSlot.Contains(name) && names.Contains(DesignTokens.PascalCase(name)))
                yield return (name, value);
        }

        foreach ((string name, string value) in Tokens.Group("opacity"))
        {
            if (names.Contains(DesignTokens.PascalCase(name)))
                yield return (name, value);
        }
    }
}
