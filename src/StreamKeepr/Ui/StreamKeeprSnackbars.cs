using MudBlazor;

namespace StreamKeepr.Ui;

/// <summary>
/// StreamKeepr's snackbar rules (design system <b>Feedback</b> card) on top of MudBlazor's
/// <see cref="ISnackbar"/>: bottom left, one visible at a time, about 4s, and an action rather
/// than a bare close icon. Snackbars confirm something already done; errors also belong next
/// to the thing that failed.
/// </summary>
public static class StreamKeeprSnackbars
{
    /// <summary>How long a snackbar stays visible.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(4);

    /// <summary>Applies the app-wide defaults; <c>Program.cs</c> and the UI tests share it.</summary>
    public static void Configure(SnackbarConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        configuration.PositionClass = Defaults.Classes.Position.BottomLeft;
        configuration.MaxDisplayedSnackbars = 1;
        configuration.NewestOnTop = false;
        // Two equal confirmations in a row (pausing two channels) each get their turn,
        // as they did before MudBlazor.
        configuration.PreventDuplicates = false;
        configuration.ShowCloseIcon = false;
        configuration.HideIcon = true;
        configuration.SnackbarVariant = Variant.Outlined;
        configuration.VisibleStateDuration = (int)Duration.TotalMilliseconds;
        // MudBlazor's default 95% lets the page show through and lowers the text's contrast.
        configuration.MaximumOpacity = 100;
    }

    /// <summary>Shows <paramref name="text"/>, with at most one action.</summary>
    /// <exception cref="ArgumentException">
    /// The text is blank, or only one of <paramref name="actionLabel"/> and <paramref name="action"/> is given.
    /// </exception>
    public static void Show(
        this ISnackbar snackbar,
        string text,
        string? actionLabel = null,
        Func<Task>? action = null)
    {
        ArgumentNullException.ThrowIfNull(snackbar);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if ((actionLabel is null) != (action is null))
            throw new ArgumentException("A snackbar action needs both a label and a callback.", nameof(action));

        if (actionLabel is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(actionLabel);

        snackbar.Add(text, Severity.Normal, options =>
        {
            if (action is null)
                return;

            options.Action = actionLabel;
            options.ActionColor = Color.Primary;
            options.ActionVariant = Variant.Text;
            options.OnClick = _ => action();
        });
    }
}
