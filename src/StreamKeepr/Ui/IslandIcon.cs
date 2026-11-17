using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace StreamKeepr.Ui;

/// <summary>
/// A MudBlazor Material Rounded glyph as a plain inline <c>&lt;svg&gt;</c>, for the JS-owned islands
/// (player, stats, chat) that must not contain MudBlazor components (ADR 0012, issue #180).
/// </summary>
/// <remarks>
/// The <c>data-icon</c> value is the <c>Icons.Material.Rounded</c> field name. Modules swap glyphs
/// from <c>wwwroot/js/island-icons.js</c>, which mirrors the same markup and names; IslandIconTests
/// keeps that copy equal to the MudBlazor constants.
/// </remarks>
public static class IslandIcon
{
    private static readonly ConcurrentDictionary<string, MarkupString> Cache = new(StringComparer.Ordinal);

    /// <summary>The decorative SVG for <paramref name="name"/>, e.g. <c>PlayArrow</c>.</summary>
    /// <exception cref="ArgumentException">No Material Rounded glyph has that name.</exception>
    public static MarkupString Svg(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Cache.GetOrAdd(name, static key =>
        {
            FieldInfo? field = typeof(MudBlazor.Icons.Material.Rounded).GetField(key, BindingFlags.Public | BindingFlags.Static);
            if (field?.GetValue(null) is not string glyph)
                throw new ArgumentException($"Icons.Material.Rounded has no glyph named '{key}'.", nameof(name));

            return new MarkupString(
                $"<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\" focusable=\"false\" data-icon=\"{key}\">{glyph}</svg>");
        });
    }
}
