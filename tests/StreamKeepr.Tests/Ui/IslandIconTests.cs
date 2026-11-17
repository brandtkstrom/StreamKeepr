using System.Reflection;
using System.Text.RegularExpressions;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

/// <summary>
/// The JS islands (player, stats, chat) draw MudBlazor's Material Rounded glyphs as inline SVG
/// without MudBlazor components. Razor renders them from the MudBlazor constants; the modules
/// swap them from a copy in <c>wwwroot/js/island-icons.js</c>. These tests keep both in step.
/// </summary>
public sealed partial class IslandIconTests
{
    [Fact]
    public void Every_js_glyph_is_the_MudBlazor_rounded_glyph_of_the_same_name()
    {
        string path = Path.Combine(TestPaths.RepositoryRoot(), "src", "StreamKeepr", "wwwroot", "js", "island-icons.js");
        MatchCollection entries = Entry().Matches(File.ReadAllText(path));

        Assert.NotEmpty(entries);
        foreach (Match entry in entries)
        {
            string name = entry.Groups["name"].Value;
            FieldInfo? field = typeof(MudBlazor.Icons.Material.Rounded).GetField(name);
            Assert.True(field is not null, $"Icons.Material.Rounded.{name} does not exist.");
            Assert.Equal(field.GetValue(null), entry.Groups["glyph"].Value);
        }
    }

    [Fact]
    public void The_js_svg_wrapper_matches_the_razor_one()
    {
        string path = Path.Combine(TestPaths.RepositoryRoot(), "src", "StreamKeepr", "wwwroot", "js", "island-icons.js");
        Match wrapper = Wrapper().Match(File.ReadAllText(path));

        Assert.True(wrapper.Success, "island-icons.js must declare SVG_ATTRIBUTES.");
        Assert.StartsWith(
            $"<svg {wrapper.Groups["attributes"].Value} data-icon=\"Check\">",
            IslandIcon.Svg("Check").Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_a_decorative_svg_named_by_its_hook()
    {
        string markup = IslandIcon.Svg("PlayArrow").Value;

        Assert.StartsWith("<svg ", markup, StringComparison.Ordinal);
        Assert.Contains("viewBox=\"0 0 24 24\"", markup, StringComparison.Ordinal);
        Assert.Contains("aria-hidden=\"true\"", markup, StringComparison.Ordinal);
        Assert.Contains("focusable=\"false\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-icon=\"PlayArrow\"", markup, StringComparison.Ordinal);
        Assert.Contains(MudBlazor.Icons.Material.Rounded.PlayArrow, markup, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_glyph_fails_fast() =>
        Assert.Throws<ArgumentException>(() => IslandIcon.Svg("NotAnIcon"));

    /// <summary>The attributes every module-built icon carries before its <c>data-icon</c>.</summary>
    [GeneratedRegex(@"^const SVG_ATTRIBUTES = '(?<attributes>[^']*)';", RegexOptions.Multiline)]
    private static partial Regex Wrapper();

    /// <summary>One <c>Name: 'glyph',</c> line of the frozen ICONS object.</summary>
    [GeneratedRegex(@"^\s*(?<name>[A-Z]\w*):\s*'(?<glyph><[^']*)',?\s*$", RegexOptions.Multiline)]
    private static partial Regex Entry();
}
