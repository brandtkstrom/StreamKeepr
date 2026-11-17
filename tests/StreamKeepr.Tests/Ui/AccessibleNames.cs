using AngleSharp.Dom;

namespace StreamKeepr.Tests.Ui;

/// <summary>Finds controls by role and accessible name (ADR 0012 ground rules).</summary>
internal static class AccessibleNames
{
    /// <summary>
    /// The element's accessible name, in the order the accessible-name algorithm uses:
    /// <c>aria-labelledby</c>, then <c>aria-label</c>, then its text without <c>aria-hidden</c>
    /// or <c>hidden</c> content (such as a Material Symbols ligature). bUnit has no layout, so
    /// content hidden only by CSS still counts, and <c>title</c> is not used as a fallback.
    /// </summary>
    public static string AccessibleName(this IElement element)
    {
        if (element.GetAttribute("aria-labelledby") is { Length: > 0 } ids && element.Owner is { } document)
        {
            return string.Join(
                " ",
                ids.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(id => document.GetElementById(id)?.TextContent.Trim())
                    .Where(text => !string.IsNullOrEmpty(text)));
        }

        if (element.GetAttribute("aria-label") is { Length: > 0 } label)
            return label.Trim();

        var copy = (IElement)element.Clone();
        foreach (IElement hidden in copy.QuerySelectorAll("[aria-hidden='true'], [hidden]").ToList())
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
