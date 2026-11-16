using MudBlazor;

namespace StreamKeepr.Ui;

/// <summary>
/// The design system's <b>Dialog</b> card options for a destructive confirmation: extra-small and
/// full width, closed by Escape or the backdrop only while no server call is in flight.
/// </summary>
public static class ConfirmDialogs
{
    private static readonly DialogOptions Idle = Create(busy: false);
    private static readonly DialogOptions Busy = Create(busy: true);

    /// <summary>The options for a confirmation that is (or is not) waiting on the server.</summary>
    public static DialogOptions Options(bool busy) => busy ? Busy : Idle;

    private static DialogOptions Create(bool busy) => new()
    {
        MaxWidth = MaxWidth.ExtraSmall,
        FullWidth = true,
        CloseOnEscapeKey = !busy,
        BackdropClick = !busy,
        CloseButton = false,
    };
}
