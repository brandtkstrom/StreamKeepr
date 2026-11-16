using System.Text.RegularExpressions;

namespace StreamKeepr.Tests;

/// <summary>
/// Enforces the selector rule in docs/testing-contract.md: bUnit and Playwright tests find
/// elements by role and accessible name, then by <c>data-*</c> hooks, and never select or
/// assert a styling class. Styling classes change with the MudBlazor migration (ADR 0012);
/// behaviour tests must survive that unchanged. The guard flags <c>sk-*</c> and <c>mud-*</c>
/// class selectors and class attributes, and any class-list or <c>class</c> attribute read,
/// which also covers unprefixed modifiers such as <c>rail</c>. Unprefixed class selectors
/// (<c>.dot</c>) cannot be told from other dotted text, so review still owns those.
/// </summary>
/// <remarks>
/// The Vitest suite under <c>tests/js</c> is out of scope: its fixtures reproduce the markup
/// that the production JavaScript modules themselves query by class, so the class is part of
/// the module's input contract rather than a test's choice of selector.
/// </remarks>
public sealed partial class TestSelectorGuardTests
{
    private static readonly string[] GuardedProjects = ["StreamKeepr.Tests", "StreamKeepr.E2E"];

    [Fact]
    public void No_test_selects_or_asserts_a_styling_class()
    {
        string root = TestPaths.RepositoryRoot();
        var violations = new List<string>();
        foreach (string project in GuardedProjects)
        {
            string directory = Path.Combine(root, "tests", project);
            foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file);
                if (IsBuildOutput(relative) || Path.GetFileName(file) == $"{nameof(TestSelectorGuardTests)}.cs")
                    continue;

                string[] lines = File.ReadAllLines(file);
                for (int index = 0; index < lines.Length; index++)
                {
                    foreach (string hit in StylingClassReferences(lines[index]))
                        violations.Add($"{relative}:{index + 1}: {hit}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Tests must find elements by role and accessible name, then data-* hooks, never by a styling class "
            + $"(docs/testing-contract.md). {violations.Count} reference(s):{Environment.NewLine}"
            + string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData("cut.Find(\".sk-snackbar\")", "sk-snackbar")]
    [InlineData("page.Locator(\"nav#sk-drawer a.sk-row\")", "sk-row")]
    [InlineData("page.Locator(\".mud-nav-link\")", "mud-nav-link")]
    [InlineData("document.querySelector('.sk-player').getBoundingClientRect()", "sk-player")]
    [InlineData("Assert.Contains(\"sk-btn\", button.ClassList);", "ClassList")]
    [InlineData("Assert.True(el.ClassList.Contains(\"sk-visually-hidden\"));", "ClassList")]
    [InlineData("await Assertions.Expect(row).ToHaveClassAsync(\"mud-selected\");", "ToHaveClassAsync")]
    [InlineData("Assert.Contains(\"class=\\\"sk-drawer rail\\\"\", html);", "sk-drawer")]
    [InlineData("page.Locator(\"[class*='sk-chip']\")", "sk-chip")]
    [InlineData("Assert.True(drawer.ClassList.Contains(\"rail\"));", "ClassList")]
    [InlineData("await drawer.GetAttributeAsync(\"class\") is { } c && c.Contains(\"rail\")", "GetAttributeAsync(\"class\")")]
    [InlineData("player.EvaluateAsync<bool>(\"p => p.classList.contains('idle')\")", "classList")]
    public void Detects_styling_class_references(string line, string expected) =>
        Assert.Contains(expected, StylingClassReferences(line));

    [Theory]
    [InlineData("cut.Find(\"[data-sk-stats]\")")]
    [InlineData("page.Locator(\"[data-sk='chat-log']\")")]
    [InlineData("cookie => cookie.Name == \"sk-auth\"")]
    [InlineData("request.Headers.Add(\"Cookie\", $\"sk-ui={cookie}\");")]
    [InlineData("Path.Combine(Path.GetTempPath(), $\"sk-export-{Guid.NewGuid():N}.mp4\")")]
    [InlineData("page.GetByRole(AriaRole.Navigation, new() { Name = \"Library\" })")]
    [InlineData("cut.Find(\"#sk-group-alpha\")")]
    [InlineData("Assert.True(button.HasAttribute(\"data-active\"));")]
    public void Ignores_hooks_ids_and_cookie_names(string line) =>
        Assert.Empty(StylingClassReferences(line));

    /// <summary>The styling classes, class-list APIs and class attribute reads on a line.</summary>
    internal static IReadOnlyList<string> StylingClassReferences(string line)
    {
        var hits = new List<string>();
        foreach (Match match in ClassSelector().Matches(line))
            hits.Add(match.Groups["name"].Value);
        foreach (Match match in ClassAttribute().Matches(line))
            hits.Add(match.Groups["name"].Value);
        foreach (Match match in ClassApi().Matches(line))
            hits.Add(match.Value);

        return hits.Distinct().ToArray();
    }

    private static bool IsBuildOutput(string relative)
    {
        string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin") || segments.Contains("obj");
    }

    /// <summary>A CSS class selector such as <c>.sk-row</c> or <c>a.mud-nav-link</c>.</summary>
    [GeneratedRegex(@"\.(?<name>(?:sk|mud)-[A-Za-z0-9_-]+)")]
    private static partial Regex ClassSelector();

    /// <summary>A class attribute match such as <c>class="sk-drawer"</c> or <c>[class*='sk-chip']</c>.</summary>
    [GeneratedRegex(@"\bclass\s*[~*^$|]?=\s*\\?[""'][^""'\\]*?(?<![\w-])(?<name>(?:sk|mud)-[A-Za-z0-9_-]+)")]
    private static partial Regex ClassAttribute();

    /// <summary>
    /// A class-list API (AngleSharp ClassList/ClassName, DOM classList/className, Playwright
    /// ToHaveClass) or a read of the <c>class</c> attribute.
    /// </summary>
    [GeneratedRegex(@"\b(?:ClassList|ClassName|classList|className|HasClass|ToHaveClass\w*)\b|\bGetAttribute(?:Async)?\(""class""\)")]
    private static partial Regex ClassApi();
}
