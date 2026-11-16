using MudBlazor;

namespace StreamKeepr.Ui;

/// <summary>
/// The app's one <see cref="MudTheme"/>, transcribed by hand from <c>design-system/tokens.json</c>
/// (see <c>design-system/mudblazor-theme.md</c>). The app is dark only, so <c>PaletteLight</c> keeps
/// MudBlazor's defaults and is never shown. <c>StreamKeeprThemeTests</c> fails when this file and
/// <c>tokens.json</c> disagree: change both together.
/// </summary>
public static class StreamKeeprTheme
{
    public static readonly MudTheme Theme = new()
    {
        // Every value is a MudBlazor.Colors constant or a MudBlazor 9.12.0 PaletteDark default.
        PaletteDark = new PaletteDark
        {
            Primary = Colors.Purple.Accent1,
            PrimaryContrastText = Colors.Gray.Darken4,
            Secondary = Colors.Cyan.Accent3,
            SecondaryContrastText = Colors.Gray.Darken4,
            Tertiary = Colors.Indigo.Accent1,
            TertiaryContrastText = Colors.Gray.Darken4,
            Info = Colors.LightBlue.Accent2,
            InfoContrastText = Colors.Gray.Darken4,
            Success = Colors.Green.Accent3,
            SuccessContrastText = Colors.Gray.Darken4,
            Warning = Colors.Amber.Accent3,
            WarningContrastText = Colors.Gray.Darken4,
            Error = Colors.Red.Accent1,
            ErrorContrastText = Colors.Gray.Darken4,
            Background = "#32333d",
            BackgroundGray = Colors.Gray.Darken4,
            AppbarBackground = "#27272f",
            AppbarText = Colors.Gray.Lighten4,
            DrawerBackground = "#27272f",
            DrawerText = Colors.Gray.Lighten4,
            DrawerIcon = "#adadb1",
            Surface = "#373740",
            TextPrimary = Colors.Gray.Lighten4,
            TextSecondary = Colors.Gray.Lighten1,
            TextDisabled = "rgba(255,255,255,0.38)",
            ActionDefault = "#adadb1",
            ActionDisabled = "rgba(255,255,255,0.26)",
            ActionDisabledBackground = "rgba(255,255,255,0.12)",
            LinesDefault = "rgba(255,255,255,0.12)",
            LinesInputs = Colors.Gray.Default,
            Divider = "rgba(255,255,255,0.12)",
            TableLines = "rgba(255,255,255,0.12)",
            OverlayDark = "rgba(33,33,33,0.5)",
            HoverOpacity = 0.08,
            RippleOpacity = 0.1,
        },
        // MudBlazor's default Typography (Roboto, Helvetica, Arial, sans-serif) is the type scale.
        // The one change: buttons stay in sentence case. ButtonTypography keeps every other default.
        Typography = new Typography
        {
            Button = new ButtonTypography { TextTransform = "none" },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
            AppbarHeight = "64px",
            DrawerWidthLeft = "320px",
            DrawerMiniWidthLeft = "72px",
        },
    };
}
