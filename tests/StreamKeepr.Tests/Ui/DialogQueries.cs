using AngleSharp.Dom;
using Bunit;
using MudBlazor;

namespace StreamKeepr.Tests.Ui;

/// <summary>Finds MudBlazor dialogs by role and accessible name (ADR 0012 ground rules).</summary>
internal static class DialogQueries
{
    /// <summary>The open dialog named <paramref name="name"/>, or null.</summary>
    public static IElement? OpenDialog(this IRenderedComponent<MudDialogProvider> dialogs, string name) =>
        dialogs.FindAll("[role='dialog']").SingleOrDefault(dialog =>
            dialog.GetAttribute("aria-labelledby") is { } titleId
            && dialogs.Find($"[id='{titleId}']").TextContent.Trim() == name);

    public static bool HasOpenDialog(this IRenderedComponent<MudDialogProvider> dialogs, string name) =>
        dialogs.OpenDialog(name) is not null;
}
