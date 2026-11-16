using MudBlazor;

namespace StreamKeepr.Tests.Ui;

/// <summary>Reads MudBlazor's snackbar queue the way the old <c>SnackbarService.Current</c> did.</summary>
internal static class SnackbarTestExtensions
{
    /// <summary>The message on screen now (the oldest shown), or null.</summary>
    public static string? CurrentMessage(this ISnackbar snackbar) =>
        snackbar.ShownSnackbars.FirstOrDefault()?.Message;

    /// <summary>Removes the message on screen, as its timeout would.</summary>
    public static void Dismiss(this ISnackbar snackbar)
    {
        if (snackbar.ShownSnackbars.FirstOrDefault() is { } current)
            snackbar.Remove(current);
    }
}
