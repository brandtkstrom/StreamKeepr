# MudBlazor theme

`tokens.json` is the source of truth. This page transcribes it into the `MudTheme` that MudBlazor reads, plus the few CSS variables MudBlazor has no slot for. It is checked against MudBlazor 9.12.0 (`PaletteDark` and the `MudBlazor.Colors` constants, which are plain strings that convert to `MudColor`; `Typography` slots typed `BaseTypography` with string `FontWeight`, `LineHeight` and `LetterSpacing`; `LayoutProperties`). The app pins exactly that version (ADR 0012); each upgrade is its own PR.

## Wiring

- In `Program.cs`, call `builder.Services.AddMudServices();`.
- In `App.razor`, link `_content/MudBlazor/MudBlazor.min.css` **before** the app's own `tokens.css`, `fonts.css` and `components.css`, and load `_content/MudBlazor/MudBlazor.min.js` after `blazor.web.js`.
- **Roboto.** MudBlazor's typography names Roboto but does not ship the font. StreamKeepr makes no CDN requests at runtime, so it self-hosts one variable `woff2` (`wwwroot/fonts/roboto-latin.woff2`), subset to Latin with the `tnum` feature kept, built from the official `Roboto[wdth,wght].ttf` (OFL-1.1) by `scripts/build-roboto.sh`. `fonts.css` declares it with one `@font-face` covering weights 100–900 and `font-display: swap`; the licence sits beside the file. Do not use the Google Fonts link from MudBlazor's template.
- In the layout, render `<MudThemeProvider Theme="StreamKeeprTheme.Theme" IsDarkMode="true" />`, `<MudPopoverProvider />`, `<MudDialogProvider />` and `<MudSnackbarProvider />` once, above `MudLayout`.
- Add `@using MudBlazor` to `Components/_Imports.razor`.
- The app is dark only: `IsDarkMode` is always `true`, there is no theme preference, and `PaletteLight` stays at MudBlazor's defaults, unused.

## Theme

```csharp
public static class StreamKeeprTheme
{
    public static readonly MudTheme Theme = new()
    {
        // Every value is a MudBlazor.Colors constant or a MudBlazor 9.12.0 PaletteDark default.
        PaletteDark = new PaletteDark
        {
            Primary = Colors.Purple.Accent1, PrimaryContrastText = Colors.Gray.Darken4,
            Secondary = Colors.Cyan.Accent3, SecondaryContrastText = Colors.Gray.Darken4,
            Tertiary = Colors.Indigo.Accent1, TertiaryContrastText = Colors.Gray.Darken4,
            Info = Colors.LightBlue.Accent2, InfoContrastText = Colors.Gray.Darken4,
            Success = Colors.Green.Accent3, SuccessContrastText = Colors.Gray.Darken4,
            Warning = Colors.Amber.Accent3, WarningContrastText = Colors.Gray.Darken4,
            Error = Colors.Red.Accent1, ErrorContrastText = Colors.Gray.Darken4,
            Background = "#32333d", BackgroundGray = Colors.Gray.Darken4,          // Background: PaletteDark default
            AppbarBackground = "#27272f", AppbarText = Colors.Gray.Lighten4,        // PaletteDark default ground
            DrawerBackground = "#27272f", DrawerText = Colors.Gray.Lighten4, DrawerIcon = "#adadb1",
            Surface = "#373740",                                                     // PaletteDark default
            TextPrimary = Colors.Gray.Lighten4, TextSecondary = Colors.Gray.Lighten1, TextDisabled = "rgba(255,255,255,0.38)",
            ActionDefault = "#adadb1", ActionDisabled = "rgba(255,255,255,0.26)", ActionDisabledBackground = "rgba(255,255,255,0.12)",
            LinesDefault = "rgba(255,255,255,0.12)", LinesInputs = Colors.Gray.Default,
            Divider = "rgba(255,255,255,0.12)", TableLines = "rgba(255,255,255,0.12)",
            OverlayDark = "rgba(33,33,33,0.5)",
            HoverOpacity = 0.08, RippleOpacity = 0.1,
        },
        // MudBlazor's default Typography (Roboto, Helvetica, Arial, sans-serif) is the system's type scale.
        // The one change: buttons stay in sentence case. ButtonTypography's constructor keeps every other default.
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
```

## What MudBlazor has no slot for

These are compiled from `tokens.json` into the app's `tokens.css`, on `:root`, next to the variables MudBlazor emits:

| Token | Used for |
|---|---|
| `surface-raised`, `selection` | stat tiles; the selected library row and current chapter |
| `channel-1` … `channel-8`, `channel-contrast-text` | avatar fills and chat names, picked by a stable hash of the login |
| `rec`, `rec-contrast-text` | REC dot, REC chip, live-edge tick, live avatar ring |
| `highlight`, `highlight-contrast-text` | `MudHighlighter` marks: `Class="sk-mark"` lands on each `<mark>` |
| `twitch`, `twitch-hover`, `twitch-contrast-text` | the Connect with Twitch button |
| `player-*` | the player island and its menus |
| `tooltip-background`, `tooltip-text` | `.mud-tooltip.mud-tooltip-default { background: var(--tooltip-background); color: var(--tooltip-text); }` |
| `text-label`, `text-wordmark`, `text-timecode`, `text-stat-value`, `text-stat-hero` | the type styles outside Mud's `Typo` enum, applied with classes; the three data styles also set `font-variant-numeric: tabular-nums` |

## Rules

- **One source.** The values above are `tokens.json` resolved: write them as the `MudBlazor.Colors` constants the source tokens name, so a reader can trace each one. `src/StreamKeepr/Ui/StreamKeeprTheme.cs` is hand-written from it, and `StreamKeeprThemeTests` parses `tokens.json` and asserts every Palette value, the layout sizes and the typography. `tokens.css` is generated by `scripts/build-tokens.mjs`, and a Vitest drift test fails when the committed file differs. Never edit only one side.
- In Razor, colour through MudBlazor's API (`Color="Color.Primary"`, `Typo="Typo.h5"`) or the Mud CSS variables (`var(--mud-palette-primary)`, `var(--mud-palette-surface)`, `var(--mud-palette-text-secondary)`). Never use raw hex.
- Keep MudBlazor's own stylesheet untouched. Restyle through the theme first, then through app CSS scoped to a class StreamKeepr owns (`.sk-panel`, `.sk-row`). Never write `!important` over `.mud-*` internals.
- MudBlazor needs the interactive circuit for behaviour. Things that must work without it, such as a library row link, the Connect with Twitch link and the Disconnect form, stay real `<a>` and `<form>` elements: `MudButton Href=…`, `MudLink`, or plain markup with Mud classes.
