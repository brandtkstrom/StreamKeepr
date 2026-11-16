using MudBlazor;

namespace StreamKeepr.Ui;

/// <summary>
/// The design system's <b>Dialog</b> card options for a destructive confirmation (used by
/// <c>ConfirmDialog</c>): extra small and full width. <c>ConfirmDialog</c> sets MudDialog.DefaultFocus to its first control (Cancel); the options' DefaultFocus does not reach an inline dialog.
/// </summary>
/// <remarks>
/// MudBlazor renders an open dialog with the options it was shown with, so they cannot change
/// while a server call runs. MudBlazor's own Escape handling is therefore off (<c>ConfirmDialog</c>
/// handles the key), and <see cref="DialogOptions.BackdropClick"/> only routes the click to
/// <c>ConfirmDialog</c>'s handler; it closes on either only while idle.
/// </remarks>
public static class ConfirmDialogOptions
{
    public static readonly DialogOptions Value = new()
    {
        MaxWidth = MaxWidth.ExtraSmall,
        FullWidth = true,
        CloseOnEscapeKey = false,
        BackdropClick = true,
        CloseButton = false,
    };
}
