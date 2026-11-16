using System.Text.RegularExpressions;

namespace StreamKeepr.Tests;

/// <summary>
/// Enforces the selector rule in docs/testing-contract.md: bUnit and Playwright tests find
/// elements by role and accessible name, then by <c>data-*</c> hooks, and never select or
/// assert a styling class (<c>sk-*</c> or <c>mud-*</c>). Styling classes change with the
/// MudBlazor migration (ADR 0011); behaviour tests must survive that unchanged.
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
    [InlineData("Assert.Contains(\"sk-btn\", button.ClassList);", "sk-btn")]
    [InlineData("Assert.True(el.ClassList.Contains(\"sk-visually-hidden\"));", "sk-visually-hidden")]
    [InlineData("await Assertions.Expect(row).ToHaveClassAsync(\"mud-selected\");", "mud-selected")]
    [InlineData("Assert.Contains(\"class=\\\"sk-drawer rail\\\"\", html);", "sk-drawer")]
    [InlineData("page.Locator(\"[class*='sk-chip']\")", "sk-chip")]
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
    public void Ignores_hooks_ids_and_cookie_names(string line) =>
        Assert.Empty(StylingClassReferences(line));

    /// <summary>The styling classes a line selects or asserts.</summary>
    internal static IReadOnlyList<string> StylingClassReferences(string line)
    {
        var hits = new List<string>();
        foreach (Match match in ClassSelector().Matches(line))
            hits.Add(match.Groups["name"].Value);
        foreach (Match match in ClassAttribute().Matches(line))
            hits.Add(match.Groups["name"].Value);
        if (ClassApi().IsMatch(line))
        {
            foreach (Match match in QuotedClassToken().Matches(line))
                hits.Add(match.Groups["name"].Value);
        }

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

    /// <summary>A class-list API: AngleSharp ClassList/ClassName, DOM classList, Playwright ToHaveClass.</summary>
    [GeneratedRegex(@"\b(?:ClassList|ClassName|classList|className|HasClass|ToHaveClass\w*)\b")]
    private static partial Regex ClassApi();

    /// <summary>An <c>sk-</c> or <c>mud-</c> token at the start of a word inside a string literal.</summary>
    [GeneratedRegex(@"[""'][^""']*?(?<![\w-])(?<name>(?:sk|mud)-[A-Za-z0-9_-]+)")]
    private static partial Regex QuotedClassToken();
}
