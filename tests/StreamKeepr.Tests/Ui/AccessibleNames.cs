using AngleSharp.Dom;

namespace StreamKeepr.Tests.Ui;

/// <summary>Finds controls by role and accessible name (ADR 0012 ground rules).</summary>
internal static class AccessibleNames
{
    /// <summary>
    /// The element's text as a screen reader names it: aria-hidden content, such as a
    /// Material Symbols ligature or a decorative icon, is left out.
    /// </summary>
    public static string AccessibleName(this IElement element)
    {
        var copy = (IElement)element.Clone();
        foreach (IElement hidden in copy.QuerySelectorAll("[aria-hidden='true']").ToList())
            hidden.Remove();

        return copy.TextContent.Trim();
    }

    /// <summary>The single <paramref name="tag"/> under <paramref name="scope"/> named <paramref name="name"/>.</summary>
    public static IElement Named(this IParentNode scope, string tag, string name) =>
        scope.QuerySelectorAll(tag).Single(element => element.AccessibleName() == name);

    /// <summary>Whether any <paramref name="tag"/> under <paramref name="scope"/> is named <paramref name="name"/>.</summary>
    public static bool HasNamed(this IParentNode scope, string tag, string name) =>
        scope.QuerySelectorAll(tag).Any(element => element.AccessibleName() == name);
}
