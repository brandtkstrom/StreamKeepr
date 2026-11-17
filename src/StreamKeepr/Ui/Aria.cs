namespace StreamKeepr.Ui;

/// <summary>ARIA attribute values Blazor would otherwise render wrongly.</summary>
public static class Aria
{
    /// <summary>
    /// <c>"true"</c>, <c>"false"</c>, or null to omit the attribute. Blazor renders a bool
    /// attribute value as presence (<c>true</c> → empty, <c>false</c> → absent), which is
    /// wrong for tri-state ARIA attributes such as <c>aria-expanded</c>.
    /// </summary>
    public static string? Bool(bool? value) => value switch
    {
        true => "true",
        false => "false",
        null => null,
    };
}
